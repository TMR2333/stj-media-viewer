import Foundation

// MARK: - 素材

/// 一条素材（与 Windows 版 catalog.db 的 assets 表字段保持兼容）。
public struct Asset: Identifiable, Hashable, Sendable {
    public var id: String
    public var libraryID: String
    public var path: String
    public var fileName: String
    public var relativePath: String?
    public var kind: Int
    public var formatCode: String
    public var sizeBytes: Int64
    public var fileModifiedAt: Date?
    public var width: Int
    public var height: Int
    public var durationSeconds: Double
    public var capturedAt: Date?
    public var capturedDay: String?
    public var country: String?
    public var province: String?
    public var city: String?
    public var latitude: Double?
    public var longitude: Double?
    public var deviceKey: String?
    public var cameraMake: String?
    public var cameraModel: String?
    public var lensModel: String?
    public var focalLength: Double?
    public var aperture: Double?
    public var iso: Int?
    public var title: String?
    public var caption: String?
    public var keywords: [String]
    public var themes: [String]
    public var tone: String?
    public var rating: Int
    public var colorLabel: String?
    /// 三态：nil 表示“未标记”，0 是废片，1 是选用。
    public var isPick: Int?
    public var postProcess: Int
    public var isLog: Bool
    public var logProfile: String?
    public var xmpPath: String?
    public var thumbKey: String?
    public var lut: String?
    public var rotationDegrees: Int
    public var groupID: String?
    public var isGroupCover: Bool
    public var companions: [String]
    public var lastError: String?
    /// AI 分类：一句话主体描述（Windows `ai_subject`）。
    public var aiSubject: String?
    /// AI 分类：情绪词（Windows `ai_mood`）。
    public var aiMood: String?
    /// AI 质量分 0~1（Windows `quality_score`）。
    public var qualityScore: Double?

    public init(id: String = UUID().uuidString,
                libraryID: String = "default",
                path: String,
                fileName: String,
                relativePath: String? = nil,
                kind: Int = 1,
                formatCode: String = "jpeg",
                sizeBytes: Int64 = 0,
                fileModifiedAt: Date? = nil,
                width: Int = 0,
                height: Int = 0,
                durationSeconds: Double = 0,
                capturedAt: Date? = nil,
                capturedDay: String? = nil,
                country: String? = nil,
                province: String? = nil,
                city: String? = nil,
                latitude: Double? = nil,
                longitude: Double? = nil,
                deviceKey: String? = nil,
                cameraMake: String? = nil,
                cameraModel: String? = nil,
                lensModel: String? = nil,
                focalLength: Double? = nil,
                aperture: Double? = nil,
                iso: Int? = nil,
                title: String? = nil,
                caption: String? = nil,
                keywords: [String] = [],
                themes: [String] = [],
                tone: String? = nil,
                rating: Int = 0,
                colorLabel: String? = nil,
                isPick: Int? = nil,
                postProcess: Int = 1,
                isLog: Bool = false,
                logProfile: String? = nil,
                xmpPath: String? = nil,
                thumbKey: String? = nil,
                lut: String? = nil,
                rotationDegrees: Int = 0,
                groupID: String? = nil,
                isGroupCover: Bool = false,
                companions: [String] = [],
                lastError: String? = nil,
                aiSubject: String? = nil,
                aiMood: String? = nil,
                qualityScore: Double? = nil) {
        self.id = id
        self.libraryID = libraryID
        self.path = path
        self.fileName = fileName
        self.relativePath = relativePath
        self.kind = kind
        self.formatCode = formatCode
        self.sizeBytes = sizeBytes
        self.fileModifiedAt = fileModifiedAt
        self.width = width
        self.height = height
        self.durationSeconds = durationSeconds
        self.capturedAt = capturedAt
        self.capturedDay = capturedDay
        self.country = country
        self.province = province
        self.city = city
        self.latitude = latitude
        self.longitude = longitude
        self.deviceKey = deviceKey
        self.cameraMake = cameraMake
        self.cameraModel = cameraModel
        self.lensModel = lensModel
        self.focalLength = focalLength
        self.aperture = aperture
        self.iso = iso
        self.title = title
        self.caption = caption
        self.keywords = keywords
        self.themes = themes
        self.tone = tone
        self.rating = rating
        self.colorLabel = colorLabel
        self.isPick = isPick
        self.postProcess = postProcess
        self.isLog = isLog
        self.logProfile = logProfile
        self.xmpPath = xmpPath
        self.thumbKey = thumbKey
        self.lut = lut
        self.rotationDegrees = rotationDegrees
        self.groupID = groupID
        self.isGroupCover = isGroupCover
        self.companions = companions
        self.lastError = lastError
        self.aiSubject = aiSubject
        self.aiMood = aiMood
        self.qualityScore = qualityScore
    }

    // MARK: 展示辅助

    public var fileURL: URL { URL(fileURLWithPath: path) }
    public var isVideo: Bool { kind == 2 }
    public var isRaw: Bool {
        let rawFormats: Set<String> = ["cr2", "cr3", "nef", "arw", "dng", "raf", "orf", "rw2", "pef", "srw", "3fr", "iiq", "x3f"]
        return rawFormats.contains(formatCode.lowercased())
    }

    public var dimensionText: String {
        guard width > 0 && height > 0 else { return "—" }
        return "\(width) × \(height)"
    }

    public var sizeText: String {
        ByteCountFormatter.string(fromByteCount: sizeBytes, countStyle: .file)
    }

    public var durationText: String? {
        guard durationSeconds > 0 else { return nil }
        let total = Int(durationSeconds.rounded())
        return String(format: "%d:%02d", total / 60, total % 60)
    }

    public var capturedText: String {
        guard let capturedAt else { return capturedDay ?? "—" }
        let f = DateFormatter()
        f.locale = Locale(identifier: "zh_CN")
        f.dateFormat = "yyyy-MM-dd HH:mm"
        return f.string(from: capturedAt)
    }

    public var locationText: String {
        let parts = [country, province, city].compactMap { value -> String? in
            guard let value, !value.isEmpty else { return nil }
            return value
        }
        return parts.isEmpty ? "—" : parts.joined(separator: " · ")
    }

    public var pickText: String {
        switch isPick {
        case 1: return "选用"
        case 0: return "废片"
        default: return "未标记"
        }
    }

    public var postProcessText: String {
        switch postProcess {
        case 2: return "已调色"
        case 3: return "已导出"
        default: return "未调色"
        }
    }

    public var kindText: String { isVideo ? "视频" : "图片" }

    /// 相同时刻存在 RAW 与 JPG 时，网格里只显示主图（由合并组负责折叠）。
    public var displayThemes: String {
        themes.isEmpty ? "" : themes.joined(separator: " / ")
    }
}

// MARK: - 三态修改值

/// 批量/单条修改某个字段：不改 / 清空 / 设置。用于区分“清空”与“未提供”。
public enum PatchValue<Value> {
    case unchanged
    case clear
    case set(Value)

    public var isUnchanged: Bool {
        if case .unchanged = self { return true }
        return false
    }
}

public struct AssetPatch {
    public var rating: Int?
    public var colorLabel: PatchValue<String> = .unchanged
    public var isPick: PatchValue<Int> = .unchanged
    public var city: PatchValue<String> = .unchanged
    public var themes: PatchValue<[String]> = .unchanged
    public var tone: PatchValue<String> = .unchanged
    public var isLog: PatchValue<Bool> = .unchanged
    public var postProcess: Int?
    public var aiSubject: PatchValue<String> = .unchanged
    public var aiMood: PatchValue<String> = .unchanged
    public var keywords: PatchValue<[String]> = .unchanged
    public var qualityScore: Double?
    public var country: PatchValue<String> = .unchanged
    public var province: PatchValue<String> = .unchanged

    public init() {}

    public var isEmpty: Bool {
        rating == nil && colorLabel.isUnchanged && isPick.isUnchanged && city.isUnchanged
            && themes.isUnchanged && tone.isUnchanged && isLog.isUnchanged && postProcess == nil
            && country.isUnchanged && province.isUnchanged
            && aiSubject.isUnchanged && aiMood.isUnchanged && keywords.isUnchanged
            && qualityScore == nil
    }
}

// MARK: - 合集 / 合并组 / 库

public struct AssetCollection: Identifiable, Hashable, Sendable {
    public var id: String
    public var name: String
    public var detail: String?
    public var count: Int
    public var sortOrder: Int

    public init(id: String, name: String, detail: String? = nil, count: Int = 0, sortOrder: Int = 0) {
        self.id = id
        self.name = name
        self.detail = detail
        self.count = count
        self.sortOrder = sortOrder
    }
}

public struct MergeGroup: Identifiable, Hashable, Sendable {
    public var id: String
    public var name: String
    public var coverAssetID: String?
    public var count: Int
    public var coverFileName: String?

    public init(id: String, name: String, coverAssetID: String? = nil, count: Int = 0, coverFileName: String? = nil) {
        self.id = id
        self.name = name
        self.coverAssetID = coverAssetID
        self.count = count
        self.coverFileName = coverFileName
    }
}

public struct LibraryRoot: Identifiable, Hashable, Sendable {
    public var id: String
    public var name: String
    public var rootPath: String
    public var count: Int

    public init(id: String, name: String, rootPath: String, count: Int = 0) {
        self.id = id
        self.name = name
        self.rootPath = rootPath
        self.count = count
    }
}

// MARK: - 筛选

public struct AssetFilter: Equatable, Sendable {
    public var searchText: String = ""
    public var kinds: Set<Int> = []
    public var formats: Set<String> = []
    public var devices: Set<String> = []
    public var cities: Set<String> = []
    public var years: Set<String> = []
    public var themes: Set<String> = []
    public var tones: Set<String> = []
    public var ratings: Set<Int> = []
    public var colorLabels: Set<String> = []
    /// 1 = 只看选用，0 = 只看废片。
    public var pick: Int?
    public var postProcesses: Set<Int> = []
    public var logMode: Bool?
    public var onlyCover: Bool = true
    /// “未填写”快捷筛选：与具体值并列取并集，对应 Windows 的 __unset__ 伪值。
    public var unsetDimensions: Set<FilterDimension> = []
    public var collectionIDs: Set<String> = []
    public var mergeGroupIDs: Set<String> = []
    /// 折叠 RAW+JPG 配对：只显示 RAW 封面，隐藏同目录同名的 JPG 伴生文件。
    /// 这是显示偏好而不是筛选条件，不计入 `isEmpty` / `activeCount`。
    public var foldRawPairs: Bool = false

    public init() {}

    public var isEmpty: Bool {
        searchText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && kinds.isEmpty && formats.isEmpty && devices.isEmpty && cities.isEmpty
            && years.isEmpty && themes.isEmpty && tones.isEmpty && ratings.isEmpty
            && colorLabels.isEmpty && pick == nil && postProcesses.isEmpty && logMode == nil
            && unsetDimensions.isEmpty && collectionIDs.isEmpty && mergeGroupIDs.isEmpty
    }

    public var activeCount: Int {
        var count = 0
        if !searchText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { count += 1 }
        count += kinds.count + formats.count + devices.count + cities.count + years.count
        count += themes.count + tones.count + ratings.count + colorLabels.count
        count += unsetDimensions.count + collectionIDs.count + mergeGroupIDs.count
        if pick != nil { count += 1 }
        if !postProcesses.isEmpty { count += 1 }
        if logMode != nil { count += 1 }
        return count
    }
}

public enum FilterDimension: String, CaseIterable, Identifiable, Sendable {
    case kind, format, year, city, country, device, theme, tone, rating, colorLabel, collection
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .kind: return "类型"
        case .format: return "格式"
        case .year: return "年份"
        case .city: return "城市"
        case .country: return "国家"
        case .device: return "相机型号"
        case .theme: return "主题"
        case .tone: return "影调"
        case .rating: return "评分"
        case .colorLabel: return "色标"
        case .collection: return "合集"
        }
    }
}

// MARK: - 排序

public enum SortKey: String, CaseIterable, Identifiable, Sendable {
    case captured
    case fileName
    case modified
    case size
    case rating
    case indexed

    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .captured: return "拍摄时间"
        case .fileName: return "文件名"
        case .modified: return "文件修改时间"
        case .size: return "文件大小"
        case .rating: return "评分"
        case .indexed: return "入库时间"
        }
    }
}

public struct SortDescriptor: Equatable, Sendable {
    public var key: SortKey
    public var ascending: Bool

    public init(key: SortKey = .captured, ascending: Bool = false) {
        self.key = key
        self.ascending = ascending
    }
}

// MARK: - 分面

public struct FacetItem: Identifiable, Hashable, Sendable {
    public var value: String
    public var count: Int
    public var id: String { value }
    public init(value: String, count: Int) {
        self.value = value
        self.count = count
    }
}

public struct Facets: Sendable {
    public var kinds: [FacetItem] = []
    public var formats: [FacetItem] = []
    public var years: [FacetItem] = []
    public var cities: [FacetItem] = []
    public var countries: [FacetItem] = []
    public var devices: [FacetItem] = []
    public var themes: [FacetItem] = []
    public var tones: [FacetItem] = []
    public var ratings: [FacetItem] = []
    public var colorLabels: [FacetItem] = []
    public var picked = 0
    public var unpicked = 0
    public var rejected = 0
    public var unprocessed = 0
    public var processed = 0
    public var logCount = 0
    public var total = 0
    public var unsetCounts: [FilterDimension: Int] = [:]

    public init() {}
}

// MARK: - 视图设置

public enum LibraryDensity: String, CaseIterable, Identifiable, Sendable {
    case small, medium, large, list
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .small: return "小"
        case .medium: return "中"
        case .large: return "大"
        case .list: return "列表"
        }
    }
    public var thumbnailMinWidth: CGFloat {
        switch self {
        case .small: return 120
        case .medium: return 168
        case .large: return 232
        case .list: return 0
        }
    }
    public var isList: Bool { self == .list }
}

public enum AppearanceMode: String, CaseIterable, Identifiable, Sendable {
    case system, light, dark
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .system: return "跟随系统"
        case .light: return "浅色"
        case .dark: return "深色"
        }
    }
}

public enum SidebarSection: String, CaseIterable, Identifiable, Sendable {
    case all, pick, rejected, uncategorized
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .all: return "全部素材"
        case .pick: return "精选"
        case .rejected: return "废片"
        case .uncategorized: return "未归类"
        }
    }
    public var symbol: String {
        switch self {
        case .all: return "square.grid.2x2"
        case .pick: return "star.fill"
        case .rejected: return "trash"
        case .uncategorized: return "questionmark.folder"
        }
    }
}