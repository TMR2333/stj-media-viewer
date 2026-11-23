import AVFoundation
import Combine
import CoreGraphics
import Foundation
import ImageIO
import QuickLookThumbnailing
import Security

// MARK: - 日志

/// 应用日志：写入 ~/Library/Logs/STJMediaViewer/app.log，并镜像到系统日志。
/// 要求：导航面包屑与网络请求都以一行摘要形式记录，方便事后排查。
public enum AppLog {
    public static let directoryURL: URL = {
        #if os(macOS)
        let base = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/STJMediaViewer", isDirectory: true)
        #else
        // iOS / iPadOS：写进 App 容器的 Library/Logs（同一套路径语义，可在「文件」App 里导出）。
        let support = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory())
        let base = support.appendingPathComponent("Logs/STJMediaViewer", isDirectory: true)
        #endif
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base
    }()

    public static var fileURL: URL { directoryURL.appendingPathComponent("app.log") }

    private static let queue = DispatchQueue(label: "com.stjarnaperspective.stjmediaviewer.log")
    private static let stampFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return formatter
    }()

    private static let maxFileSize = 5 * 1024 * 1024

    private static func write(level: String, message: String) {
        let line = "[\(stampFormatter.string(from: Date()))] [\(level)] \(message)\n"
        queue.async {
            let url = fileURL
            if let size = try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int,
               size > maxFileSize {
                let backup = directoryURL.appendingPathComponent("app.log.1")
                try? FileManager.default.removeItem(at: backup)
                try? FileManager.default.moveItem(at: url, to: backup)
            }
            if !FileManager.default.fileExists(atPath: url.path) {
                try? line.data(using: .utf8)?.write(to: url, options: .atomic)
            } else if let handle = try? FileHandle(forWritingTo: url) {
                defer { try? handle.close() }
                try? handle.seekToEnd()
                try? handle.write(contentsOf: Data(line.utf8))
            }
        }
    }

    public static func info(_ message: String) { write(level: "INFO", message: message) }
    public static func debug(_ message: String) { write(level: "DEBUG", message: message) }
    public static func error(_ message: String) { write(level: "ERROR", message: message) }

    /// 导航面包屑：打开素材库、切换视图、筛选、选中、展开合并组等。
    public static func breadcrumb(_ message: String) { write(level: "NAV", message: message) }

    /// 网络请求一行摘要。
    public static func network(method: String, url: String, status: Int?, bytes: Int, milliseconds: Int, note: String? = nil) {
        var line = "\(method) \(url) → \(status.map(String.init) ?? "ERR") \(bytes)B \(milliseconds)ms"
        if let note, !note.isEmpty { line += " · \(note)" }
        write(level: "NET", message: line)
    }
}

// MARK: - 钥匙串

public enum Keychain {
    private static let service = "com.stjarnaperspective.stjmediaviewer"

    public static func set(_ value: String, for account: String) {
        let data = Data(value.utf8)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
        var insert = query
        insert[kSecValueData as String] = data
        let status = SecItemAdd(insert as CFDictionary, nil)
        if status != errSecSuccess {
            AppLog.error("钥匙串写入失败 account=\(account) status=\(status)")
        }
    }

    public static func get(_ account: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    public static func remove(_ account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}

// MARK: - 设置

/// 用户偏好：外观、密度、面板可见性、AI 端点等。API Key 单独存钥匙串。
@MainActor
public final class SettingsStore: ObservableObject {
    public static let shared = SettingsStore()

    private let defaults: UserDefaults

    @Published public var appearance: AppearanceMode {
        didSet { defaults.set(appearance.rawValue, forKey: Keys.appearance) }
    }
    @Published public var density: LibraryDensity {
        didSet { defaults.set(density.rawValue, forKey: Keys.density) }
    }
    @Published public var showsInspector: Bool {
        didSet { defaults.set(showsInspector, forKey: Keys.showsInspector) }
    }
    @Published public var showsSidebar: Bool {
        didSet { defaults.set(showsSidebar, forKey: Keys.showsSidebar) }
    }
    @Published public var sortKey: SortKey {
        didSet { defaults.set(sortKey.rawValue, forKey: Keys.sortKey) }
    }
    @Published public var sortAscending: Bool {
        didSet { defaults.set(sortAscending, forKey: Keys.sortAscending) }
    }
    @Published public var excludesScreenshotsInGallery: Bool {
        didSet { defaults.set(excludesScreenshotsInGallery, forKey: Keys.galleryExcludesScreenshots) }
    }
    /// 合并 RAW+JPG 配对：网格里只显示 RAW 封面（角标标注 RAW+JPG），隐藏同名 JPG 伴生文件。
    @Published public var foldRawPairs: Bool {
        didSet { defaults.set(foldRawPairs, forKey: Keys.foldRawPairs) }
    }
    /// 图片与视频分库显示：侧栏拆成「图片素材 / 视频素材」两个入口。
    @Published public var splitMediaKinds: Bool {
        didSet { defaults.set(splitMediaKinds, forKey: Keys.splitMediaKinds) }
    }
    @Published public var aiEndpoint: String {
        didSet { defaults.set(aiEndpoint, forKey: Keys.aiEndpoint) }
    }
    @Published public var aiModel: String {
        didSet { defaults.set(aiModel, forKey: Keys.aiModel) }
    }
    @Published public var webdavEndpoint: String {
        didSet { defaults.set(webdavEndpoint, forKey: Keys.webdavEndpoint) }
    }
    @Published public var webdavUsername: String {
        didSet { defaults.set(webdavUsername, forKey: Keys.webdavUsername) }
    }
    /// 远端根目录（可空），例如 `/dav/stj`。
    @Published public var webdavRemoteRoot: String {
        didSet { defaults.set(webdavRemoteRoot, forKey: Keys.webdavRemoteRoot) }
    }
    /// 云端目录布局（CloudLayout.rawValue）。
    @Published public var webdavLayout: String {
        didSet { defaults.set(webdavLayout, forKey: Keys.webdavLayout) }
    }
    /// 导出水印文本（空表示不加水印）。
    @Published public var watermarkText: String {
        didSet { defaults.set(watermarkText, forKey: Keys.watermarkText) }
    }
    /// 水印位置（WatermarkStyle.Position.rawValue）。
    @Published public var watermarkPosition: String {
        didSet { defaults.set(watermarkPosition, forKey: Keys.watermarkPosition) }
    }
    @Published public var watermarkOpacity: Double {
        didSet { defaults.set(watermarkOpacity, forKey: Keys.watermarkOpacity) }
    }
    @Published public var watermarkIncludesCameraModel: Bool {
        didSet { defaults.set(watermarkIncludesCameraModel, forKey: Keys.watermarkIncludesCamera) }
    }
    /// 是否完成首次引导。
    @Published public var hasCompletedOnboarding: Bool {
        didSet { defaults.set(hasCompletedOnboarding, forKey: Keys.onboardingDone) }
    }
    @Published public var hasAPIKey: Bool
    @Published public var hasWebDAVPassword: Bool

    private enum Keys {
        static let appearance = "appearance"
        static let density = "density"
        static let showsInspector = "showsInspector"
        static let showsSidebar = "showsSidebar"
        static let sortKey = "sortKey"
        static let sortAscending = "sortAscending"
        static let galleryExcludesScreenshots = "galleryExcludesScreenshots"
        static let foldRawPairs = "foldRawPairs"
        static let splitMediaKinds = "splitMediaKinds"
        static let aiEndpoint = "aiEndpoint"
        static let aiModel = "aiModel"
        static let webdavEndpoint = "webdavEndpoint"
        static let webdavUsername = "webdavUsername"
        static let webdavRemoteRoot = "webdavRemoteRoot"
        static let webdavLayout = "webdavLayout"
        static let watermarkText = "watermarkText"
        static let watermarkPosition = "watermarkPosition"
        static let watermarkOpacity = "watermarkOpacity"
        static let watermarkIncludesCamera = "watermarkIncludesCamera"
        static let onboardingDone = "hasCompletedOnboarding"
        static let apiKeyAccount = "ai.apiKey"
        static let webdavPasswordAccount = "webdav.password"
    }

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        self.appearance = AppearanceMode(rawValue: defaults.string(forKey: Keys.appearance) ?? "system") ?? .system
        self.density = LibraryDensity(rawValue: defaults.string(forKey: Keys.density) ?? "medium") ?? .medium
        self.showsInspector = defaults.object(forKey: Keys.showsInspector) as? Bool ?? true
        self.showsSidebar = defaults.object(forKey: Keys.showsSidebar) as? Bool ?? true
        self.sortKey = SortKey(rawValue: defaults.string(forKey: Keys.sortKey) ?? "captured") ?? .captured
        self.sortAscending = defaults.object(forKey: Keys.sortAscending) as? Bool ?? false
        self.excludesScreenshotsInGallery = defaults.object(forKey: Keys.galleryExcludesScreenshots) as? Bool ?? true
        self.foldRawPairs = defaults.object(forKey: Keys.foldRawPairs) as? Bool ?? true
        self.splitMediaKinds = defaults.object(forKey: Keys.splitMediaKinds) as? Bool ?? true
        self.aiEndpoint = defaults.string(forKey: Keys.aiEndpoint) ?? "https://api.openai.com/v1"
        self.aiModel = defaults.string(forKey: Keys.aiModel) ?? "gpt-4o-mini"
        self.webdavEndpoint = defaults.string(forKey: Keys.webdavEndpoint) ?? ""
        self.webdavUsername = defaults.string(forKey: Keys.webdavUsername) ?? ""
        self.webdavRemoteRoot = defaults.string(forKey: Keys.webdavRemoteRoot) ?? ""
        self.webdavLayout = defaults.string(forKey: Keys.webdavLayout) ?? CloudLayout.byDate.rawValue
        self.watermarkText = defaults.string(forKey: Keys.watermarkText) ?? "STJ Media Viewer"
        self.watermarkPosition = defaults.string(forKey: Keys.watermarkPosition) ?? WatermarkStyle.Position.bottomRight.rawValue
        self.watermarkOpacity = defaults.object(forKey: Keys.watermarkOpacity) as? Double ?? 0.75
        self.watermarkIncludesCameraModel = defaults.object(forKey: Keys.watermarkIncludesCamera) as? Bool ?? false
        self.hasCompletedOnboarding = defaults.bool(forKey: Keys.onboardingDone)
        self.hasAPIKey = Keychain.get(Keys.apiKeyAccount)?.isEmpty == false
        self.hasWebDAVPassword = Keychain.get(Keys.webdavPasswordAccount)?.isEmpty == false
    }

    public var apiKey: String? {
        Keychain.get(Keys.apiKeyAccount)
    }

    /// 当前设置对应的水印样式。
    public var watermarkStyle: WatermarkStyle {
        WatermarkStyle(text: watermarkText,
                       position: WatermarkStyle.Position(rawValue: watermarkPosition) ?? .bottomRight,
                       opacity: min(max(watermarkOpacity, 0), 1),
                       includesCameraModel: watermarkIncludesCameraModel)
    }

    public func setAPIKey(_ value: String) {
        if value.isEmpty {
            Keychain.remove(Keys.apiKeyAccount)
        } else {
            Keychain.set(value, for: Keys.apiKeyAccount)
        }
        hasAPIKey = !value.isEmpty
    }

    // MARK: WebDAV

    public var webdavPassword: String? {
        Keychain.get(Keys.webdavPasswordAccount)
    }

    public func setWebDAVPassword(_ value: String) {
        if value.isEmpty {
            Keychain.remove(Keys.webdavPasswordAccount)
        } else {
            Keychain.set(value, for: Keys.webdavPasswordAccount)
        }
        hasWebDAVPassword = !value.isEmpty
    }

    /// 装配 WebDAV 参数；地址无效或没填口令时返回 nil。
    public func webdavCredentials() -> WebDAVCredentials? {
        let trimmed = webdavEndpoint.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: trimmed), url.scheme != nil else { return nil }
        var resolvedPassword = webdavPassword
        #if DEBUG
        // 仅 Debug 构建：自动化验收时用启动参数传口令，避免钥匙串授权对话框阻塞（无辅助功能权限无法点击）。
        if let override = UserDefaults.standard.string(forKey: "webdavPassword"), !override.isEmpty {
            resolvedPassword = override
        }
        #endif
        guard let password = resolvedPassword, !password.isEmpty else { return nil }
        return WebDAVCredentials(endpoint: url,
                                 username: webdavUsername,
                                 password: password,
                                 remoteRoot: webdavRemoteRoot)
    }

    public var cloudLayout: CloudLayout {
        CloudLayout(rawValue: webdavLayout) ?? .byDate
    }
}

// MARK: - 缩略图

public enum ThumbnailSource: String, Sendable {
    case cached
    case quickLook
    case fileSystem
}

public struct ThumbnailResult: Sendable {
    public let image: CGImage
    public let source: ThumbnailSource
}

/// 缩略图缓存：优先使用 Windows 版生成的 Cache/thumbs 缓存，
/// 缺失时回退 QuickLook 缩略图，绝不静默吞掉失败（按文件名记日志）。
public final class ThumbnailStore: @unchecked Sendable {
    public static let shared = ThumbnailStore()

    private final class ImageBox {
        let image: CGImage
        init(_ image: CGImage) { self.image = image }
    }

    private let cache = NSCache<NSString, ImageBox>()
    private let ioQueue = DispatchQueue(label: "com.stjarnaperspective.stjmediaviewer.thumbs", attributes: .concurrent)
    private var cacheDirectory: URL?
    private var thumbnailIndex: [String: URL]?
    private let lock = NSLock()

    public init() {
        cache.totalCostLimit = 128 * 1024 * 1024
    }

    public func configure(cacheDirectory: URL?) {
        guard let cacheDirectory else { return }
        lock.lock()
        self.cacheDirectory = cacheDirectory
        self.thumbnailIndex = nil
        cache.removeAllObjects()
        lock.unlock()
    }

    public var currentCacheDirectory: URL? {
        lock.lock()
        defer { lock.unlock() }
        return cacheDirectory
    }

    /// 扫描缓存目录，返回可命中的缩略图数量（用于状态栏展示）。
    public func cachedThumbnailCount() -> Int {
        rebuildIndexIfNeeded()
        lock.lock()
        defer { lock.unlock() }
        return thumbnailIndex?.count ?? 0
    }

    /// 一次性计算「缓存命中 → QuickLook 兜底」的素材数量，避免逐张列目录。
    public func cachedHitCount(for assets: [Asset]) -> Int {
        rebuildIndexIfNeeded()
        guard !assets.isEmpty else { return 0 }
        return assets.reduce(into: 0) { partial, asset in
            if cachedThumbnailURL(for: asset) != nil { partial += 1 }
        }
    }

    /// 数据库里的缩略图键可能带 `_修订号` 后缀，缓存文件名里的键是纯 40 位 SHA-1。
    static func normalizedKey(_ raw: String) -> String {
        var key = raw.trimmingCharacters(in: .whitespaces).lowercased()
        for suffix in ["_lut", "_edit", "_edited"] where key.hasSuffix(suffix) {
            key = String(key.dropLast(suffix.count))
        }
        if key.count > 40 {
            let head = String(key.prefix(40))
            if head.allSatisfy({ $0.isHexDigit }) { key = head }
        }
        return key
    }

    private func rebuildIndexIfNeeded() {
        lock.lock()
        let needsRebuild = thumbnailIndex == nil
        let directory = cacheDirectory
        lock.unlock()
        guard needsRebuild, let directory else { return }
        var index: [String: URL] = [:]
        let files = (try? FileManager.default.contentsOfDirectory(atPath: directory.path)) ?? []
        for name in files {
            let lower = name.lowercased()
            guard lower.hasSuffix(".jpg") || lower.hasSuffix(".png") else { continue }
            let url = directory.appendingPathComponent(name)
            if let info = ThumbnailNaming.parse(name) {
                // 同一路径键可能有多个尺寸/变体，选尺寸最大的那个。
                if let existing = index[info.key],
                   let old = ThumbnailNaming.parse(existing.lastPathComponent),
                   old.size >= info.size {
                    continue
                }
                index[info.key] = url
            }
        }
        lock.lock()
        if thumbnailIndex == nil { thumbnailIndex = index }
        lock.unlock()
    }

    public func image(for asset: Asset, targetSize: CGFloat = 360) async -> ThumbnailResult? {
        let key = "\(asset.path)|\(asset.thumbKeyCandidate)|\(Int(targetSize))"
        if let cached = cache.object(forKey: key as NSString) {
            return ThumbnailResult(image: cached.image, source: .cached)
        }
        if let url = cachedThumbnailURL(for: asset), let image = await loadImage(at: url, asset: asset) {
            cache.setObject(ImageBox(image), forKey: key as NSString)
            return ThumbnailResult(image: image, source: .cached)
        }
        if let image = await quickLookImage(for: asset) {
            cache.setObject(ImageBox(image), forKey: key as NSString)
            return ThumbnailResult(image: image, source: .quickLook)
        }
        if asset.isVideo, let image = Self.videoFrameImage(at: asset.fileURL) {
            cache.setObject(ImageBox(image), forKey: key as NSString)
            return ThumbnailResult(image: image, source: .quickLook)
        }
        if let image = await loadImage(at: asset.fileURL, asset: asset) {
            cache.setObject(ImageBox(image), forKey: key as NSString)
            return ThumbnailResult(image: image, source: .fileSystem)
        }
        return nil
    }

    /// 缩略图键：路径 + 大小 + 修改时间 + 编辑状态 + LUT，与 Windows 版算法保持同构。
    public func thumbnailKey(for asset: Asset) -> String {
        let modified = asset.fileModifiedAt.map { String(Int($0.timeIntervalSince1970 * 1000)) } ?? "0"
        let raw = "\(asset.path)|\(asset.sizeBytes)|\(modified)|\(asset.postProcess)|\(asset.logProfile ?? "")|\(asset.lut ?? "")"
        return raw
    }

    public struct ThumbVariant: Sendable {
        public let url: URL
        public let sizeClass: Int
        public let imageWidth: Int
        public let imageHeight: Int
    }

    public func cachedThumbnailURL(for asset: Asset) -> URL? {
        rebuildIndexIfNeeded()
        lock.lock()
        let index = thumbnailIndex
        lock.unlock()
        guard let index, !index.isEmpty else { return nil }
        var keys: [String] = []
        let thumbKey = ThumbnailStore.normalizedKey(asset.thumbKeyCandidate)
        if !thumbKey.isEmpty { keys.append(thumbKey) }
        let hashed = asset.pathHasher()
        if hashed != thumbKey { keys.append(hashed) }
        for key in keys {
            if let url = index[key] { return url }
        }
        return nil
    }

    private func loadImage(at url: URL, asset: Asset) async -> CGImage? {
        await withCheckedContinuation { continuation in
            ioQueue.async {
                let image = ImageCodecs.decode(at: url, maxPixelSize: nil)
                if image == nil {
                    ThumbnailStore.logFailure("缓存缩略图解码失败：\(url.lastPathComponent)")
                }
                continuation.resume(returning: image)
            }
        }
    }

    /// 视频帧兜底：模拟器/无 QuickLook 插件时用 AVFoundation 抽首帧。
    /// （macOS 与 iOS 都可用，失败时返回 nil 并只在 debug 日志里留痕。）
    static func videoFrameImage(at url: URL, maxPixel: Int = 640) -> CGImage? {
        let asset = AVURLAsset(url: url)
        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        generator.maximumSize = CGSize(width: maxPixel, height: maxPixel)
        let time = CMTime(seconds: 0.1, preferredTimescale: 600)
        guard let cgImage = try? generator.copyCGImage(at: time, actualTime: nil) else {
            ThumbnailStore.logFailure("视频抽帧失败：\(url.lastPathComponent)")
            return nil
        }
        return cgImage
    }

    public func quickLookImage(for asset: Asset, maxPixel: Int = 1024) async -> CGImage? {
        let url = asset.fileURL
        guard FileManager.default.fileExists(atPath: url.path) else {
            ThumbnailStore.logFailure("文件不存在，无法生成 QuickLook 缩略图：\(url.lastPathComponent)")
            return nil
        }
        let size = CGSize(width: maxPixel, height: maxPixel)
        let request = QLThumbnailGenerator.Request(fileAt: url, size: size, scale: 1,
                                                  representationTypes: [.thumbnail, .lowQualityThumbnail])
        do {
            let representation = try await QLThumbnailGenerator.shared.generateBestRepresentation(for: request)
            return representation.cgImage
        } catch {
            ThumbnailStore.logFailure("QuickLook 生成失败：\(url.lastPathComponent) — \(error.localizedDescription)")
            return nil
        }
    }

    public static func logFailure(_ message: String) {
        AppLog.error("缩略图：\(message)")
        FileHandle.standardError.write(Data(("缩略图：\(message)\n").utf8))
    }
}

enum ThumbnailNaming {
    /// 文件名约定：`{40 hex 键}_{修订号}_{尺寸}[_lut].jpg`。
    static func parse(_ fileName: String) -> (key: String, revision: String, size: Int)? {
        var name = (fileName as NSString).deletingPathExtension
        if name.lowercased().hasSuffix("_lut") {
            name = String(name.dropLast(4))
        }
        let parts = name.split(separator: "_")
        guard parts.count >= 2, parts[0].count == 40,
              parts[0].allSatisfy({ $0.isHexDigit }) else { return nil }
        guard let size = parts.compactMap({ Int($0) }).last else { return nil }
        let revision = parts.count >= 3 ? String(parts[1]) : ""
        return (String(parts[0]).lowercased(), revision, size)
    }
}

// MARK: - XMP 侧车

/// 全屏画廊用的原始图像加载：优先用 ImageIO 直接解出大尺寸缩略图，
/// 避免把 6000 万像素原图整个读进内存。视频与无解码器的文件返回 nil，
/// 由调用方回退到 QuickLook 缩略图。
public enum ImageLoader {
    public static func previewImage(at url: URL, maxPixelSize: Int = 2400) -> CGImage? {
        ImageCodecs.decode(at: url, maxPixelSize: maxPixelSize)
    }
}

public enum XMPWriter {
    public enum XMPError: LocalizedError {
        case cannotEncode
        case writeFailed(String)
        public var errorDescription: String? {
            switch self {
            case .cannotEncode: return "无法构造 XMP 内容。"
            case .writeFailed(let message): return "写入 XMP 失败：\(message)"
            }
        }
    }

    public static func xmpURL(for asset: Asset) -> URL {
        let base = asset.fileURL.deletingPathExtension()
        return base.appendingPathExtension("xmp")
    }

    public static func packet(for asset: Asset) -> String {
        let escaped = { (value: String) in xmlEscape(value) }
        let subjects = asset.keywords.map { "   <rdf:li>\(escaped($0))</rdf:li>" }.joined(separator: "\n")
        let now = ISO8601DateFormatter().string(from: Date())
        return """
        <?xpacket begin="\u{FEFF}" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="STJ Media Viewer">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:dc="http://purl.org/dc/elements/1.1/"
            xmlns:xmp="http://ns.adobe.com/xap/1.0/"
            xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
            xmp:Rating="\(asset.rating)"
            xmp:Label="\(escaped(asset.colorLabel ?? ""))"
            xmp:CreateDate="\(now)"
            photoshop:City="\(escaped(asset.city ?? ""))"
            photoshop:Country="\(escaped(asset.country ?? ""))">
           <dc:subject><rdf:Bag>
        \(subjects)
           </rdf:Bag></dc:subject>
           <dc:title><rdf:Alt><rdf:li xml:lang="x-default">\(escaped(asset.title ?? asset.fileName))</rdf:li></rdf:Alt></dc:title>
           <dc:description><rdf:Alt><rdf:li xml:lang="x-default">\(escaped(asset.caption ?? ""))</rdf:li></rdf:Alt></dc:description>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """
    }

    @discardableResult
    public static func write(for assets: [Asset]) throws -> [URL] {
        var written: [URL] = []
        for asset in assets {
            let url = xmpURL(for: asset)
            let content = packet(for: asset)
            guard let data = content.data(using: .utf8) else { throw XMPError.cannotEncode }
            do {
                try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                        withIntermediateDirectories: true)
                try data.write(to: url, options: .atomic)
            } catch {
                throw XMPError.writeFailed(error.localizedDescription)
            }
            written.append(url)
        }
        return written
    }

    private static func xmlEscape(_ value: String) -> String {
        value
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&apos;")
    }
}

// MARK: - 素材便捷扩展

extension Asset {
    /// 直接命中 DB 的 thumb_key；没有时按路径哈希推断缓存文件名前缀。
    public var thumbKeyCandidate: String {
        if let key = thumbKey, !key.isEmpty { return key.lowercased() }
        return pathHasher()
    }

    public func pathHasher() -> String {
        var hash = [UInt8]()
        let path = self.path
        if let cached = ThumbnailHashCache.shared.hash(for: path) {
            return cached
        }
        hash = SHA1.hexDigest(Data(path.utf8))
        let value = hash.map { String(format: "%02x", $0) }.joined()
        ThumbnailHashCache.shared.store(value, for: path)
        return value
    }
}

final class ThumbnailHashCache: @unchecked Sendable {
    static let shared = ThumbnailHashCache()
    private let lock = NSLock()
    private var values: [String: String] = [:]
    func hash(for path: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        return values[path]
    }
    func store(_ hash: String, for path: String) {
        lock.lock(); defer { lock.unlock() }
        values[path] = hash
    }
}

// MARK: - SHA1（不引入第三方库）

public enum SHA1 {
    public static func hexDigest(_ data: Data) -> [UInt8] {
        var message = [UInt8](data)
        let bitLength = UInt64(message.count) * 8
        message.append(0x80)
        while message.count % 64 != 56 {
            message.append(0)
        }
        for shift in stride(from: 56, through: 0, by: -8) {
            message.append(UInt8((bitLength >> UInt64(shift)) & 0xFF))
        }

        var h0: UInt32 = 0x67452301
        var h1: UInt32 = 0xEFCDAB89
        var h2: UInt32 = 0x98BADCFE
        var h3: UInt32 = 0x10325476
        var h4: UInt32 = 0xC3D2E1F0

        var chunk = [UInt32](repeating: 0, count: 80)
        var offset = 0
        while offset < message.count {
            for i in 0..<16 {
                let start = offset + i * 4
                chunk[i] = (UInt32(message[start]) << 24)
                    | (UInt32(message[start + 1]) << 16)
                    | (UInt32(message[start + 2]) << 8)
                    | UInt32(message[start + 3])
            }
            for i in 16..<80 {
                chunk[i] = rotateLeft(chunk[i - 3] ^ chunk[i - 8] ^ chunk[i - 14] ^ chunk[i - 16], by: 1)
            }
            var a = h0, b = h1, c = h2, d = h3, e = h4
            for i in 0..<80 {
                var f: UInt32
                var k: UInt32
                switch i {
                case 0..<20: f = (b & c) | ((~b) & d); k = 0x5A827999
                case 20..<40: f = b ^ c ^ d; k = 0x6ED9EBA1
                case 40..<60: f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC
                default: f = b ^ c ^ d; k = 0xCA62C1D6
                }
                let temp = rotateLeft(a, by: 5) &+ f &+ e &+ k &+ chunk[i]
                e = d; d = c; c = rotateLeft(b, by: 30); b = a; a = temp
            }
            h0 = h0 &+ a; h1 = h1 &+ b; h2 = h2 &+ c; h3 = h3 &+ d; h4 = h4 &+ e
            offset += 64
        }
        var out = [UInt8]()
        for value in [h0, h1, h2, h3, h4] {
            out.append(UInt8((value >> 24) & 0xFF))
            out.append(UInt8((value >> 16) & 0xFF))
            out.append(UInt8((value >> 8) & 0xFF))
            out.append(UInt8(value & 0xFF))
        }
        return out
    }

    public static func hexString(_ data: Data) -> String {
        hexDigest(data).map { String(format: "%02x", $0) }.joined()
    }

    private static func rotateLeft(_ value: UInt32, by amount: UInt32) -> UInt32 {
        (value << amount) | (value >> (32 - amount))
    }
}