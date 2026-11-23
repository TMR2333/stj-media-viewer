import Foundation
import CoreGraphics

/// 云相册：把 WebDAV 远端目录当作一个相册来浏览、预览、导入与导出。
///
/// 设计要点：
/// - 与 `CloudSync`（上传）合并为一套入口：浏览远端 + 导入到工作区 + 上传本地素材。
/// - 远端只读：浏览不会改远端内容，除非你主动上传。
/// - 缩略图/原图的本地缓存放在应用工作区目录下，不触碰素材目录。
public struct CloudAlbumItem: Identifiable, Sendable, Hashable {
    /// 相对远端根目录的路径。
    public let path: String
    public let name: String
    public let size: Int64
    public let modified: Date?
    public let isDirectory: Bool

    public init(path: String, name: String, size: Int64, modified: Date?, isDirectory: Bool) {
        self.path = path
        self.name = name
        self.size = size
        self.modified = modified
        self.isDirectory = isDirectory
    }

    public var id: String { path }
    public var fileExtension: String { (name as NSString).pathExtension.lowercased() }
    public var isImage: Bool { CloudAlbum.imageExtensions.contains(fileExtension) }
    public var isVideo: Bool { CloudAlbum.videoExtensions.contains(fileExtension) }
    public var isMedia: Bool { isImage || isVideo }

    public var sizeText: String {
        ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
    }

    public var modifiedText: String? {
        guard let modified else { return nil }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "yyyy-MM-dd HH:mm"
        return formatter.string(from: modified)
    }
}

public struct CloudAlbumProgress: Sendable {
    public let completed: Int
    public let total: Int
    public let currentName: String

    public init(completed: Int, total: Int, currentName: String) {
        self.completed = completed
        self.total = total
        self.currentName = currentName
    }
}

public struct CloudAlbumImportSummary: Sendable {
    public let directory: URL
    public let downloaded: Int
    public let failed: Int
    public let failures: [String]

    public init(directory: URL, downloaded: Int, failed: Int, failures: [String]) {
        self.directory = directory
        self.downloaded = downloaded
        self.failed = failed
        self.failures = failures
    }

    public var descriptionText: String {
        if failed > 0 {
            return "已下载 \(downloaded) 个 · 失败 \(failed) 个"
        }
        return "已下载 \(downloaded) 个"
    }
}

public enum CloudAlbumError: LocalizedError {
    case noMedia
    case cacheFailed(String)

    public var errorDescription: String? {
        switch self {
        case .noMedia: return "该目录里没有可用的图片或视频。"
        case .cacheFailed(let name): return "云端文件无法缓存：\(name)"
        }
    }
}

public enum CloudAlbum {
    public static let imageExtensions: Set<String> = [
        "jpg", "jpeg", "png", "heic", "heif", "tif", "tiff", "gif", "webp", "bmp", "dng", "cr3", "cr2", "nef", "arw", "raf", "orf", "rw2",
    ]
    public static let videoExtensions: Set<String> = ["mov", "mp4", "m4v", "avi", "mkv", "webm"]

    /// 云相册导入的落地目录：优先 `~/Pictures/STJ Media Viewer`（用户容易找到），
    /// 不可写时退回应用工作区；无论如何都不写进素材目录本身。
    public static var downloadRoot: URL {
        let fileManager = FileManager.default
        let pictures = fileManager.urls(for: .picturesDirectory, in: .userDomainMask).first
        if let pictures {
            let url = pictures.appendingPathComponent("STJ Media Viewer", isDirectory: true)
            if (try? fileManager.createDirectory(at: url, withIntermediateDirectories: true)) != nil {
                return url
            }
        }
        return WorkspacePaths.directory
    }

    /// 缓存根目录（应用工作区下，不进素材目录）。
    public static var cacheRoot: URL {
        let url = WorkspacePaths.directory.appendingPathComponent("CloudCache", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    /// 递归列举远端目录下的媒体文件（最多 `maxDepth` 层，避免超大目录卡死）。
    public static func listMedia(_ client: WebDAVClient,
                                 root: String,
                                 maxDepth: Int = 4,
                                 limit: Int = 5000) async throws -> [CloudAlbumItem] {
        var results: [CloudAlbumItem] = []
        var queue: [(path: String, depth: Int)] = [(root, 0)]
        var visited = Set<String>()
        while !queue.isEmpty && results.count < limit {
            let current = queue.removeFirst()
            if visited.contains(current.path) { continue }
            visited.insert(current.path)
            let entries = try await client.listEntries(current.path)
            for entry in entries {
                if entry.isDirectory {
                    if current.depth + 1 < maxDepth {
                        queue.append((entry.path, current.depth + 1))
                    }
                    continue
                }
                let item = CloudAlbumItem(path: entry.path,
                                          name: entry.name,
                                          size: entry.size,
                                          modified: entry.modified,
                                          isDirectory: false)
                if item.isMedia { results.append(item) }
                if results.count >= limit { break }
            }
        }
        return results
    }

    // MARK: - 本地缓存

    public static func cacheURL(for item: CloudAlbumItem) -> URL {
        let key = SHA1.hexDigest(Data((item.path + "|" + String(item.size)).utf8))
        let ext = item.fileExtension.isEmpty ? "bin" : item.fileExtension
        return cacheRoot.appendingPathComponent("\(key).\(ext)")
    }

    public static func cachedFile(for item: CloudAlbumItem, fileManager: FileManager = .default) -> URL? {
        let url = cacheURL(for: item)
        return fileManager.fileExists(atPath: url.path) ? url : nil
    }

    /// 确保文件已缓存到本地（已存在则直接返回）。
    @discardableResult
    public static func ensureCached(_ item: CloudAlbumItem,
                                    client: WebDAVClient,
                                    fileManager: FileManager = .default) async throws -> URL {
        if let cached = cachedFile(for: item, fileManager: fileManager) { return cached }
        let destination = cacheURL(for: item)
        try fileManager.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        do {
            try await client.download(relativePath: item.path, to: destination)
        } catch {
            try? fileManager.removeItem(at: destination)
            throw CloudAlbumError.cacheFailed(item.name)
        }
        return destination
    }

    /// 缩略图（缓存原图后用 ImageIO 下采样，避免把原图整读进内存）。
    public static func thumbnail(for item: CloudAlbumItem,
                                 client: WebDAVClient,
                                 maxPixel: Int = 480) async -> CGImage? {
        guard let file = try? await ensureCached(item, client: client) else { return nil }
        return ImageCodecs.decode(at: file, maxPixelSize: maxPixel)
    }

    /// 预览大图（下采样到 2400px）。
    public static func previewImage(for item: CloudAlbumItem,
                                    client: WebDAVClient,
                                    maxPixel: Int = 2400) async -> CGImage? {
        guard let file = try? await ensureCached(item, client: client) else { return nil }
        if item.isVideo {
            return ThumbnailStore.videoFrameImage(at: file, maxPixel: maxPixel)
        }
        return ImageCodecs.decode(at: file, maxPixelSize: maxPixel)
    }

    // MARK: - 导入

    /// 把远端所选媒体下载到本地目录（默认工作区下的「云相册导入-时间戳」），随后交给 Importer 建索引。
    public static func importItems(_ items: [CloudAlbumItem],
                                   client: WebDAVClient,
                                   destinationRoot: URL,
                                   progress: (@Sendable (CloudAlbumProgress) -> Void)? = nil) async throws -> CloudAlbumImportSummary {
        guard !items.isEmpty else { throw CloudAlbumError.noMedia }
        let fileManager = FileManager.default
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let folder = destinationRoot.appendingPathComponent("云相册导入-\(formatter.string(from: Date()))", isDirectory: true)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)

        var downloaded = 0
        var failures: [String] = []
        for (index, item) in items.enumerated() {
            if Task.isCancelled { break }
            progress?(CloudAlbumProgress(completed: index, total: items.count, currentName: item.name))
            let destination = uniqueDestination(folder.appendingPathComponent(item.name), fileManager: fileManager)
            do {
                _ = try await client.download(relativePath: item.path, to: destination)
                downloaded += 1
            } catch {
                failures.append(item.name)
            }
        }
        progress?(CloudAlbumProgress(completed: items.count, total: items.count, currentName: ""))
        return CloudAlbumImportSummary(directory: folder,
                                       downloaded: downloaded,
                                       failed: failures.count,
                                       failures: failures)
    }

    static func uniqueDestination(_ url: URL, fileManager: FileManager) -> URL {
        guard fileManager.fileExists(atPath: url.path) else { return url }
        let base = url.deletingPathExtension().lastPathComponent
        let ext = url.pathExtension
        var index = 1
        while true {
            let candidate = url.deletingLastPathComponent()
                .appendingPathComponent("\(base)-\(index)")
                .appendingPathExtension(ext)
            if !fileManager.fileExists(atPath: candidate.path) { return candidate }
            index += 1
        }
    }

    // MARK: - 导出（复用 CloudSync 上传）

    /// 把本地素材上传到云端相册（与原有「云同步上传」合并为同一入口）。
    public static func export(assets: [Asset],
                              credentials: WebDAVCredentials,
                              configuration: URLSessionConfiguration = .ephemeral,
                              layout: CloudLayout,
                              fileManager: FileManager = .default,
                              progress: ((CloudSyncProgress) -> Void)? = nil,
                              interItemDelay: Duration = .zero) async -> CloudSyncResult {
        let plan = CloudSync.plan(assets: assets, layout: layout, fileManager: fileManager)
        return await CloudSync.upload(items: plan.0,
                                      credentials: credentials,
                                      configuration: configuration,
                                      progress: progress,
                                      interItemDelay: interItemDelay)
    }
}