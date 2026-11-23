import Foundation
import AVFoundation
import ImageIO
import CoreGraphics

// MARK: - 缩略图生成

/// 导入时生成 `Cache/thumbs/{sha1}_{revision}_{size}.jpg`，
/// 与 Windows 端 / macOS 阅读端（`ThumbnailStore`）的命名约定一致。
public enum ThumbnailMaker {
    /// 只读阅片用的两档尺寸。
    public static let sizes = [360, 640]

    /// 生成缩略图并返回 thumb_key（`{sha1}_1`，不含尺寸后缀）；失败返回 nil。
    @discardableResult
    public static func generate(
        for asset: ScannedAsset,
        cacheDirectory: URL,
        fileManager: FileManager = .default
    ) -> String? {
        // Windows 版缩略图键 = sha1(小写 full_path) + 修订号 + 尺寸
        let key = SHA1.hexDigest(Data(asset.path.path.lowercased().utf8))
            .map { String(format: "%02x", $0) }
            .joined()
        let thumbKey = "\(key)_1"
        try? fileManager.createDirectory(at: cacheDirectory, withIntermediateDirectories: true)

        let needsGeneration = sizes.contains { size in
            !fileManager.fileExists(atPath: cacheDirectory.appendingPathComponent("\(thumbKey)_\(size).jpg").path)
        }
        guard needsGeneration else { return thumbKey }

        let image: CGImage?
        if asset.kind == 2 {
            image = videoFrame(for: asset.path)
        } else {
            image = imageSourceThumbnail(for: asset.path, maxPixelSize: sizes.max() ?? 640)
        }
        guard let image else { return nil }

        for size in sizes {
            let target = cacheDirectory.appendingPathComponent("\(thumbKey)_\(size).jpg")
            if fileManager.fileExists(atPath: target.path) { continue }
            guard let scaled = scale(image, maxPixelSize: size) else { continue }
            guard let data = ImageCodecs.jpegData(scaled, quality: 0.82) else { continue }
            try? data.write(to: target)
        }
        return thumbKey
    }

    static func imageSourceThumbnail(for url: URL, maxPixelSize: Int) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixelSize
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }

    static func videoFrame(for url: URL) -> CGImage? {
        let asset = AVURLAsset(url: url)
        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        generator.maximumSize = CGSize(width: 640, height: 640)
        let duration = CMTimeGetSeconds(asset.duration)
        let time = CMTime(seconds: min(1.0, max(0.0, duration * 0.1)), preferredTimescale: 600)
        return try? generator.copyCGImage(at: time, actualTime: nil)
    }

    static func scale(_ image: CGImage, maxPixelSize: Int) -> CGImage? {
        let width = image.width
        let height = image.height
        let longest = max(width, height)
        guard longest > 0 else { return image }
        let ratio = min(1.0, Double(maxPixelSize) / Double(longest))
        let newWidth = max(1, Int((Double(width) * ratio).rounded()))
        let newHeight = max(1, Int((Double(height) * ratio).rounded()))
        if newWidth == width && newHeight == height { return image }
        let colorSpace = image.colorSpace ?? CGColorSpaceCreateDeviceRGB()
        guard let context = CGContext(
            data: nil,
            width: newWidth,
            height: newHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return image }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: newWidth, height: newHeight))
        return context.makeImage()
    }
}

// MARK: - 导入

public struct ImportProgress: Sendable {
    public var processed: Int
    public var total: Int
    public var currentFileName: String
    public init(processed: Int, total: Int, currentFileName: String) {
        self.processed = processed
        self.total = total
        self.currentFileName = currentFileName
    }
}

public struct ImportSummary: Sendable {
    public var added: Int = 0
    public var updated: Int = 0
    public var skipped: Int = 0
    public var failed: Int = 0
    public var thumbnails: Int = 0
    /// 从 `.xmp` 侧车回填到空字段的素材数量。
    public var xmpApplied: Int = 0
    public var totalAssets: Int = 0
    public var scannedFiles: Int = 0
    public var databaseURL: URL?
    public var cacheDirectory: URL?
    public var failures: [ScanFailure] = []
    public var duration: TimeInterval = 0
    public init() {}

    public var descriptionText: String {
        "新增 \(added) · 更新 \(updated) · 跳过 \(skipped) · 失败 \(failed) · 缩略图 \(thumbnails) · XMP 回填 \(xmpApplied)"
    }
}

/// 扫描目录并把媒体写入 `catalog.db`（Windows 兼容 schema）。
///
/// - 目录内没有 `catalog.db` 时按 `CatalogSchema` 新建；
/// - 已有行按 `full_path` 增量更新，大小与修改时间都没变则跳过（保留用户编辑过的评分/主题等）；
/// - 新素材生成 `Cache/thumbs` 缓存缩略图并写 `thumb_key`；
/// - 结束时刷新 `libraries` 的 `asset_count` / `total_bytes` / `last_scan_at`。
public enum Importer {
    public static let libraryRootMarker = "catalog.db"

    @discardableResult
    public static func importFolder(
        _ root: URL,
        libraryName: String? = nil,
        recursive: Bool = true,
        generateThumbnails: Bool = true,
        progress: ((ImportProgress) -> Void)? = nil
    ) throws -> ImportSummary {
        let started = Date()
        var summary = ImportSummary()
        // 解析符号链接后再扫描：避免同一素材目录经不同路径（如 /tmp 与 /private/tmp）被重复入库。
        let rootURL = root.resolvingSymlinksInPath().standardizedFileURL
        let databaseURL = rootURL.appendingPathComponent(libraryRootMarker)
        summary.databaseURL = databaseURL

        let database = try Database(path: databaseURL.path, readOnly: false)
        try CatalogSchema.create(in: database)

        let libraryID = try ensureLibrary(database, root: rootURL, name: libraryName)
        let scan = MediaScanner.scan(root: rootURL, libraryID: libraryID, recursive: recursive)
        summary.scannedFiles = scan.scannedFileCount
        summary.failures = scan.failures
        summary.failed = scan.failures.count

        let cacheDirectory = rootURL.appendingPathComponent("Cache/thumbs", isDirectory: true)
        summary.cacheDirectory = cacheDirectory

        let existingColumns = try database.tableColumns("assets")
        let existingRows = try loadExisting(database)
        let now = Date()

        try database.transaction {
            for (index, asset) in scan.assets.enumerated() {
                progress?(ImportProgress(processed: index, total: scan.assets.count, currentFileName: asset.fileName))
                if let previous = existingRows[canonicalPath(asset.path.path)] {
                    if previous.sizeBytes == asset.sizeBytes,
                       previous.modifiedMs == Int64((asset.fileModifiedAt?.timeIntervalSince1970 ?? 0) * 1000),
                       previous.thumbKey?.isEmpty == false {
                        summary.skipped += 1
                        continue
                    }
                }

                var thumbKey = existingRows[canonicalPath(asset.path.path)]?.thumbKey
                if generateThumbnails, asset.kind == 1 || asset.kind == 2 {
                    if let generated = ThumbnailMaker.generate(for: asset, cacheDirectory: cacheDirectory) {
                        thumbKey = generated
                        summary.thumbnails += 1
                    }
                }

                // XMP 侧车：只在数据库字段为空时回填，用户已有的编辑优先
                let xmp = XMPReader.metadata(forFileAt: asset.path)
                let row = Row(
                    asset: asset,
                    libraryID: libraryID,
                    thumbKey: thumbKey,
                    quickHash: quickHash(for: asset),
                    now: now,
                    xmp: xmp
                )
                if let previous = existingRows[row.path] {
                    let writableXMP = xmpOrder.filter { !previous.filled.contains($0) }
                    let include = Set(techOrder).union(writableXMP)
                    try update(database, row: row, id: previous.id, columns: existingColumns, include: include)
                    summary.updated += 1
                    if xmp != nil, !writableXMP.isEmpty {
                        summary.xmpApplied += 1
                    }
                } else {
                    try insert(database, row: row, columns: existingColumns)
                    summary.added += 1
                    if xmp != nil {
                        summary.xmpApplied += 1
                    }
                }
            }

            try refreshLibrary(database, libraryID: libraryID, root: rootURL, now: now)
        }

        summary.totalAssets = try database.scalarInt("SELECT count(*) FROM assets;")
        summary.duration = Date().timeIntervalSince(started)
        progress?(ImportProgress(processed: scan.assets.count, total: scan.assets.count, currentFileName: ""))
        return summary
    }

    // MARK: 库

    static func ensureLibrary(_ database: Database, root: URL, name: String?) throws -> String {
        let columns = try database.tableColumns("libraries")
        let hasRootPath = columns.contains("root_path")
        let query = hasRootPath
            ? "SELECT id FROM libraries WHERE path = ? OR root_path = ? LIMIT 1;"
            : "SELECT id FROM libraries WHERE path = ? LIMIT 1;"
        let binds: [Any?] = hasRootPath ? [root.path, root.path] : [root.path]
        if let existing = try database.query(query, binds: binds, map: { $0.string(0) }).first, let existing {
            return existing
        }

        let id = UUID().uuidString
        var fields = ["id", "path"]
        var values: [Any?] = [id, root.path]
        if hasRootPath {
            fields.append("root_path")
            values.append(root.path)
        }
        if columns.contains("name") {
            fields.append("name")
            values.append(name ?? root.lastPathComponent)
        }
        if columns.contains("include_subfolders") { fields.append("include_subfolders"); values.append(1) }
        if columns.contains("watch_for_changes") { fields.append("watch_for_changes"); values.append(1) }
        if columns.contains("added_at") { fields.append("added_at"); values.append(ISO8601DateFormatter().string(from: Date())) }
        if columns.contains("sort_order") { fields.append("sort_order"); values.append(0) }

        let placeholders = fields.map { _ in "?" }.joined(separator: ", ")
        let statement = try database.prepare(
            "INSERT INTO libraries(\(fields.joined(separator: ", "))) VALUES(\(placeholders));"
        )
        try statement.bindAll(values)
        _ = try statement.step()
        statement.reset()
        return id
    }

    static func refreshLibrary(_ database: Database, libraryID: String, root: URL, now: Date) throws {
        let columns = try database.tableColumns("libraries")
        let count = try database.scalarInt("SELECT count(*) FROM assets WHERE library_id = ?;", binds: [libraryID])
        let bytes = try database.scalarInt("SELECT COALESCE(sum(size_bytes), 0) FROM assets WHERE library_id = ?;", binds: [libraryID])
        var assignments: [String] = []
        var values: [Any?] = []
        if columns.contains("asset_count") { assignments.append("asset_count = ?"); values.append(count) }
        if columns.contains("total_bytes") { assignments.append("total_bytes = ?"); values.append(bytes) }
        if columns.contains("last_scan_at") { assignments.append("last_scan_at = ?"); values.append(ISO8601DateFormatter().string(from: now)) }
        guard !assignments.isEmpty else { return }
        values.append(libraryID)
        let statement = try database.prepare(
            "UPDATE libraries SET \(assignments.joined(separator: ", ")) WHERE id = ?;"
        )
        try statement.bindAll(values)
        _ = try statement.step()
        statement.reset()
    }

    // MARK: 行

    struct ExistingRow {
        var id: String
        var sizeBytes: Int64
        var modifiedMs: Int64
        var thumbKey: String?
        /// 数据库中已有非空值的“可编辑字段”，用于导入时判断哪些空字段才允许用 XMP 回填。
        var filled: Set<String> = []
    }

    /// 允许由 XMP 侧车回填的字段（与 Row 里的键保持一致）。
    static let xmpOrder = [
        "rating", "color_label", "city", "country", "province",
        "title", "caption", "creator", "keywords_json", "keywords_flat",
    ]

    /// 规范化路径：解析符号链接（/tmp → /private/tmp）并标准化，避免同一文件被重复入库。
    static func canonicalPath(_ path: String) -> String {
        URL(fileURLWithPath: path).resolvingSymlinksInPath().standardizedFileURL.path
    }

    static func loadExisting(_ database: Database) throws -> [String: ExistingRow] {
        let columns = try database.tableColumns("assets")
        let thumb = columns.contains("thumb_key") ? "thumb_key" : "NULL"
        let modified = columns.contains("file_modified_ms") ? "file_modified_ms" : "NULL"
        let checkable = xmpOrder.filter { columns.contains($0) }
        let extra = checkable.map { $0 }.joined(separator: ", ")
        let rows = try database.query(
            "SELECT id, full_path, size_bytes, \(modified), \(thumb)\(extra.isEmpty ? "" : ", " + extra) FROM assets;"
        ) { statement -> (String, ExistingRow) in
            let path = statement.string(1) ?? ""
            let id = statement.string(0) ?? ""
            var filled = Set<String>()
            for (offset, column) in checkable.enumerated() {
                let index = Int32(5 + offset)
                if let text = statement.string(index), !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    filled.insert(column)
                } else if statement.int64(index).map({ $0 > 0 }) == true && column != "keywords_json" {
                    filled.insert(column)
                }
            }
            return (path, ExistingRow(
                id: id,
                sizeBytes: Int64(statement.int64(2) ?? 0),
                modifiedMs: Int64(statement.int64(3) ?? 0),
                thumbKey: statement.string(4),
                filled: filled
            ))
        }
        var map: [String: ExistingRow] = [:]
        for (path, row) in rows {
            map[canonicalPath(path)] = row
        }
        return map
    }

    struct Row {
        var values: [String: Any?]
        var path: String
        init(asset: ScannedAsset, libraryID: String, thumbKey: String?, quickHash: String, now: Date, xmp: XMPMetadata? = nil) {
            let path = Importer.canonicalPath(asset.path.path)
            let captured = asset.capturedAt
            let iso = ISO8601DateFormatter()
            let day = DateFormatter()
            day.locale = Locale(identifier: "en_US_POSIX")
            day.dateFormat = "yyyy-MM-dd"
            day.timeZone = .current
            let modified = asset.fileModifiedAt
            self.path = path

            var values: [String: Any?] = [
                "id": asset.id,
                "library_id": libraryID,
                "full_path": path,
                "file_name": asset.fileName,
                "relative_path": asset.relativePath,
                "size_bytes": asset.sizeBytes,
                "file_modified": modified.map { iso.string(from: $0) },
                "file_modified_ms": modified.map { Int64($0.timeIntervalSince1970 * 1000) },
                "kind": asset.kind,
                "format_code": asset.formatCode,
                "mime_type": asset.mimeType,
                "image_flavor": asset.imageFlavor,
                "raw_format": asset.isRaw ? 1 : 0,
                "width": asset.width,
                "height": asset.height,
                "duration_seconds": asset.durationSeconds,
                "frame_rate": asset.frameRate,
                "camera_make": asset.cameraMake,
                "camera_model": asset.cameraModel,
                "device_key": asset.deviceKey,
                "lens_model": asset.lensModel,
                "focal_length_mm": asset.focalLength,
                "aperture_f": asset.aperture,
                "shutter_speed_seconds": asset.shutterSeconds,
                "iso": asset.iso,
                "captured_at": captured.map { iso.string(from: $0) },
                "captured_ms": captured.map { Int64($0.timeIntervalSince1970 * 1000) },
                "captured_day": captured.map { day.string(from: $0) },
                "captured_at_source": asset.capturedAtSource,
                "latitude": asset.latitude,
                "longitude": asset.longitude,
                "quick_hash": quickHash,
                "thumb_key": thumbKey,
                "indexed_at": iso.string(from: now),
                "indexed_ms": Int64(now.timeIntervalSince1970 * 1000),
                "metadata_parsed_at": iso.string(from: now),
                "rating": 0,
                "keywords_json": "[]",
                "keywords_flat": ""
            ]
            if let xmp {
                values["rating"] = xmp.rating ?? 0
                values["color_label"] = xmp.colorLabel
                values["city"] = xmp.city
                values["country"] = xmp.country
                values["province"] = xmp.province
                values["title"] = xmp.title
                values["caption"] = xmp.caption
                values["creator"] = xmp.creator
                if !xmp.keywords.isEmpty {
                    let json = (try? JSONSerialization.data(withJSONObject: xmp.keywords, options: []))
                        .flatMap { String(data: $0, encoding: .utf8) } ?? "[]"
                    values["keywords_json"] = json
                    values["keywords_flat"] = xmp.keywords.joined(separator: ", ")
                }
            }
            // 旧库（Windows 早期版本 / 样例库）可能缺列，写入时按实际列裁剪
            values["full_path"] = path
            self.values = values
        }
    }

    static let insertOrder = [
        "id", "library_id", "full_path", "file_name", "relative_path", "size_bytes",
        "file_modified", "file_modified_ms", "kind", "format_code", "mime_type",
        "image_flavor", "raw_format", "width", "height", "duration_seconds", "frame_rate",
        "camera_make", "camera_model", "device_key", "lens_model", "focal_length_mm",
        "aperture_f", "shutter_speed_seconds", "iso", "captured_at", "captured_ms",
        "captured_day", "captured_at_source", "latitude", "longitude", "quick_hash",
        "thumb_key", "indexed_at", "indexed_ms", "metadata_parsed_at",
        "rating", "color_label", "city", "country", "province",
        "title", "caption", "creator", "keywords_json", "keywords_flat"
    ]

    /// 技术元数据列：导入时始终以文件系统/EXIF 为准刷新，不包含用户可编辑字段。
    static let techOrder = insertOrder.filter { !xmpOrder.contains($0) }

    static func insert(_ database: Database, row: Row, columns: Set<String>) throws {
        let fields = insertOrder.filter { columns.contains($0) }
        let placeholders = fields.map { _ in "?" }.joined(separator: ", ")
        let statement = try database.prepare(
            "INSERT INTO assets(\(fields.joined(separator: ", "))) VALUES(\(placeholders));"
        )
        try statement.bindAll(fields.map { row.values[$0] ?? nil })
        _ = try statement.step()
        statement.reset()
    }

    static func update(_ database: Database, row: Row, id: String, columns: Set<String>, include: Set<String>? = nil) throws {
        let fields = insertOrder.filter { columns.contains($0) && $0 != "id" && (include?.contains($0) ?? true) }
        let assignments = fields.map { "\($0) = ?" }.joined(separator: ", ")
        let statement = try database.prepare("UPDATE assets SET \(assignments) WHERE id = ?;")
        var values: [Any?] = fields.map { row.values[$0] ?? nil }
        values.append(id)
        try statement.bindAll(values)
        _ = try statement.step()
        statement.reset()
    }

    static func quickHash(for asset: ScannedAsset) -> String {
        let modified = Int64((asset.fileModifiedAt?.timeIntervalSince1970 ?? 0) * 1000)
        let seed = "\(asset.path.standardizedFileURL.path)|\(asset.sizeBytes)|\(modified)"
        return SHA1.hexDigest(Data(seed.utf8)).map { String(format: "%02x", $0) }.joined()
    }
}