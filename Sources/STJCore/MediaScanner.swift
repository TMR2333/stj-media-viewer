import Foundation
import AVFoundation
import ImageIO
import UniformTypeIdentifiers

// MARK: - 扫描模型

/// 扫描到的单个媒体文件（尚未写库）。
public struct ScannedAsset: Sendable {
    public var id: String
    public var libraryID: String
    public var path: URL
    public var fileName: String
    public var relativePath: String
    public var sizeBytes: Int64
    public var fileModifiedAt: Date?
    /// 1 = 图片，2 = 视频（与 Windows 版 kind 常量一致）。
    public var kind: Int
    /// 小写扩展名，如 `jpg` / `mov` / `cr3`。
    public var formatCode: String
    public var mimeType: String?
    /// 1 = JPEG/PNG 等位图，2 = HEIF，3 = RAW（与 Windows 版 image_flavor 语义一致）。
    public var imageFlavor: Int
    public var isRaw: Bool
    public var width: Int?
    public var height: Int?
    public var durationSeconds: Double?
    public var frameRate: Double?
    public var cameraMake: String?
    public var cameraModel: String?
    public var lensModel: String?
    public var focalLength: Double?
    public var aperture: Double?
    public var shutterSeconds: Double?
    public var iso: Int?
    public var capturedAt: Date?
    public var capturedAtSource: String?
    public var latitude: Double?
    public var longitude: Double?

    public init(
        id: String = UUID().uuidString,
        libraryID: String,
        path: URL,
        fileName: String = "",
        relativePath: String = "",
        sizeBytes: Int64 = 0,
        fileModifiedAt: Date? = nil,
        kind: Int = 1,
        formatCode: String = "",
        mimeType: String? = nil,
        imageFlavor: Int = 1,
        isRaw: Bool = false,
        width: Int? = nil,
        height: Int? = nil,
        durationSeconds: Double? = nil,
        frameRate: Double? = nil,
        cameraMake: String? = nil,
        cameraModel: String? = nil,
        lensModel: String? = nil,
        focalLength: Double? = nil,
        aperture: Double? = nil,
        shutterSeconds: Double? = nil,
        iso: Int? = nil,
        capturedAt: Date? = nil,
        capturedAtSource: String? = nil,
        latitude: Double? = nil,
        longitude: Double? = nil
    ) {
        self.id = id
        self.libraryID = libraryID
        self.path = path
        self.fileName = fileName.isEmpty ? path.lastPathComponent : fileName
        self.relativePath = relativePath
        self.sizeBytes = sizeBytes
        self.fileModifiedAt = fileModifiedAt
        self.kind = kind
        self.formatCode = formatCode
        self.mimeType = mimeType
        self.imageFlavor = imageFlavor
        self.isRaw = isRaw
        self.width = width
        self.height = height
        self.durationSeconds = durationSeconds
        self.frameRate = frameRate
        self.cameraMake = cameraMake
        self.cameraModel = cameraModel
        self.lensModel = lensModel
        self.focalLength = focalLength
        self.aperture = aperture
        self.shutterSeconds = shutterSeconds
        self.iso = iso
        self.capturedAt = capturedAt
        self.capturedAtSource = capturedAtSource
        self.latitude = latitude
        self.longitude = longitude
    }

    public var deviceKey: String? {
        guard let model = cameraModel?.trimmingCharacters(in: .whitespacesAndNewlines), !model.isEmpty else {
            return nil
        }
        return model.replacingOccurrences(of: " ", with: "")
    }
}

public struct ScanFailure: Sendable, Equatable {
    public var path: String
    public var message: String
    public init(path: String, message: String) {
        self.path = path
        self.message = message
    }
}

public struct ScanResult: Sendable {
    public var assets: [ScannedAsset]
    public var failures: [ScanFailure]
    public var scannedFileCount: Int
    public init(assets: [ScannedAsset] = [], failures: [ScanFailure] = [], scannedFileCount: Int = 0) {
        self.assets = assets
        self.failures = failures
        self.scannedFileCount = scannedFileCount
    }
}

// MARK: - 扫描器

/// 目录扫描 + 元数据抽取（ImageIO / AVFoundation）。
public enum MediaScanner {
    public static let imageExtensions: Set<String> = [
        "jpg", "jpeg", "png", "heic", "heif", "tif", "tiff", "gif", "bmp", "webp"
    ]
    public static let rawExtensions: Set<String> = [
        "cr3", "cr2", "crw", "nef", "nrw", "arw", "srf", "sr2", "raf", "rw2",
        "orf", "dng", "pef", "srw", "x3f", "rwl", "mos", "mrw", "3fr", "erf",
        "mef", "iiq", "k25", "kdc", "dcr", "raw"
    ]
    public static let videoExtensions: Set<String> = ["mov", "mp4", "m4v", "avi", "mkv"]

    /// 扫描时跳过这些目录：缩略图缓存、隐藏缓存目录，避免把缓存图当成素材导入。
    public static let skippedDirectoryNames: Set<String> = ["Cache", ".thumbnails", ".stjcache", ".build"]

    /// 递归（或仅顶层）扫描目录，抽取元数据；不抛错，单个文件失败记入 failures。
    public static func scan(
        root: URL,
        libraryID: String,
        recursive: Bool = true,
        includeHidden: Bool = false
    ) -> ScanResult {
        var result = ScanResult()
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey, .contentTypeKey, .nameKey]
        let options: FileManager.DirectoryEnumerationOptions = recursive
            ? (!includeHidden ? [.skipsHiddenFiles] : [])
            : [.skipsSubdirectoryDescendants, .skipsHiddenFiles]

        guard let enumerator = FileManager.default.enumerator(
            at: root,
            includingPropertiesForKeys: keys,
            options: options
        ) else {
            result.failures.append(ScanFailure(path: root.path, message: "无法枚举目录"))
            return result
        }

        let rootPath = root.standardizedFileURL.path
        for case let fileURL as URL in enumerator {
            let relativeToRoot = relativePath(of: fileURL, rootPath: rootPath)
            if relativeToRoot.split(separator: "/").contains(where: { skippedDirectoryNames.contains(String($0)) }) {
                if let directoryEnumerator = enumerator as? FileManager.DirectoryEnumerator {
                    directoryEnumerator.skipDescendants()
                }
                continue
            }
            let values = try? fileURL.resourceValues(forKeys: Set(keys))
            if values?.isRegularFile != true { continue }
            let format = fileURL.pathExtension.lowercased()
            let isImage = imageExtensions.contains(format) || rawExtensions.contains(format)
            let isVideo = videoExtensions.contains(format)
            if !isImage && !isVideo { continue }

            result.scannedFileCount += 1
            let relative = relativePath(of: fileURL, rootPath: rootPath)
            do {
                let asset = try makeAsset(
                    url: fileURL,
                    root: root,
                    relativePath: relative,
                    libraryID: libraryID,
                    isVideo: isVideo,
                    format: format,
                    size: values?.fileSize,
                    modified: values?.contentModificationDate
                )
                result.assets.append(asset)
            } catch {
                result.failures.append(ScanFailure(path: fileURL.path, message: error.localizedDescription))
            }
        }
        result.assets.sort { $0.path.path.localizedStandardCompare($1.path.path) == .orderedAscending }
        return result
    }

    static func relativePath(of url: URL, rootPath: String) -> String {
        let path = url.standardizedFileURL.path
        guard path.hasPrefix(rootPath) else { return url.lastPathComponent }
        var relative = String(path.dropFirst(rootPath.count))
        if relative.hasPrefix("/") { relative.removeFirst() }
        return relative
    }

    static func makeAsset(
        url: URL,
        root: URL,
        relativePath: String,
        libraryID: String,
        isVideo: Bool,
        format: String,
        size: Int?,
        modified: Date?
    ) throws -> ScannedAsset {
        if isVideo {
            return videoAsset(
                url: url,
                libraryID: libraryID,
                relativePath: relativePath,
                format: format,
                size: size,
                modified: modified
            )
        }
        return try imageAsset(
            url: url,
            libraryID: libraryID,
            relativePath: relativePath,
            format: format,
            size: size,
            modified: modified
        )
    }

    // MARK: 图片

    static func imageAsset(
        url: URL,
        libraryID: String,
        relativePath: String,
        format: String,
        size: Int?,
        modified: Date?
    ) throws -> ScannedAsset {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else {
            throw ScanError.unreadable("ImageIO 无法打开文件")
        }
        let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] ?? [:]
        let pixelWidth = properties[kCGImagePropertyPixelWidth] as? Int
        let pixelHeight = properties[kCGImagePropertyPixelHeight] as? Int
        let orientation = properties[kCGImagePropertyOrientation] as? Int ?? 1
        let exif = properties[kCGImagePropertyExifDictionary] as? [CFString: Any] ?? [:]
        let tiff = properties[kCGImagePropertyTIFFDictionary] as? [CFString: Any] ?? [:]
        let gps = properties[kCGImagePropertyGPSDictionary] as? [CFString: Any] ?? [:]

        let (width, height): (Int?, Int?) = {
            guard let w = pixelWidth, let h = pixelHeight else { return (nil, nil) }
            // 5/6/7/8 表示旋转了 90°，宽高互换
            return (5...8).contains(orientation) ? (h, w) : (w, h)
        }()

        var capturedAt: Date?
        var capturedAtSource: String?
        if let raw = exif[kCGImagePropertyExifDateTimeOriginal] as? String,
           let date = Self.exifFormatter.date(from: raw) {
            capturedAt = date
            capturedAtSource = "exif"
        } else if let raw = tiff[kCGImagePropertyTIFFDateTime] as? String,
                  let date = Self.exifFormatter.date(from: raw) {
            capturedAt = date
            capturedAtSource = "exif"
        } else if let modified {
            capturedAt = modified
            capturedAtSource = "file"
        }

        let raw = rawExtensions.contains(format)
        let flavor: Int
        if raw {
            flavor = 3
        } else if format == "heic" || format == "heif" {
            flavor = 2
        } else {
            flavor = 1
        }

        var latitude: Double?
        var longitude: Double?
        if let value = gps[kCGImagePropertyGPSLatitude] as? Double,
           let ref = gps[kCGImagePropertyGPSLatitudeRef] as? String {
            latitude = ref.uppercased() == "S" ? -value : value
        }
        if let value = gps[kCGImagePropertyGPSLongitude] as? Double,
           let ref = gps[kCGImagePropertyGPSLongitudeRef] as? String {
            longitude = ref.uppercased() == "W" ? -value : value
        }

        let iso = Self.intValue(exif[kCGImagePropertyExifISOSpeedRatings])
        return ScannedAsset(
            libraryID: libraryID,
            path: url,
            relativePath: relativePath,
            sizeBytes: Int64(size ?? 0),
            fileModifiedAt: modified,
            kind: 1,
            formatCode: format,
            mimeType: UTType(filenameExtension: format)?.preferredMIMEType,
            imageFlavor: flavor,
            isRaw: raw,
            width: width,
            height: height,
            durationSeconds: nil,
            frameRate: nil,
            cameraMake: Self.stringValue(tiff[kCGImagePropertyTIFFMake]),
            cameraModel: Self.stringValue(tiff[kCGImagePropertyTIFFModel]),
            lensModel: Self.stringValue(exif[kCGImagePropertyExifLensModel]),
            focalLength: Self.doubleValue(exif[kCGImagePropertyExifFocalLength]),
            aperture: Self.doubleValue(exif[kCGImagePropertyExifFNumber]),
            shutterSeconds: Self.doubleValue(exif[kCGImagePropertyExifExposureTime]),
            iso: iso,
            capturedAt: capturedAt,
            capturedAtSource: capturedAtSource,
            latitude: latitude,
            longitude: longitude
        )
    }

    // MARK: 视频

    static func videoAsset(
        url: URL,
        libraryID: String,
        relativePath: String,
        format: String,
        size: Int?,
        modified: Date?
    ) -> ScannedAsset {
        let asset = AVURLAsset(url: url)
        let duration = CMTimeGetSeconds(asset.duration)
        var width: Int?
        var height: Int?
        var frameRate: Double?
        if let track = asset.tracks(withMediaType: .video).first {
            let transformed = track.naturalSize.applying(track.preferredTransform)
            width = Int(abs(transformed.width).rounded())
            height = Int(abs(transformed.height).rounded())
            if track.nominalFrameRate > 0 {
                frameRate = Double(round(track.nominalFrameRate * 100) / 100)
            }
            if width == 0 || height == 0 {
                width = Int(abs(track.naturalSize.width).rounded())
                height = Int(abs(track.naturalSize.height).rounded())
            }
        }
        var capturedAt: Date?
        var capturedAtSource: String?
        if let date = asset.creationDate?.dateValue {
            capturedAt = date
            capturedAtSource = "exif"
        } else if let modified {
            capturedAt = modified
            capturedAtSource = "file"
        }
        return ScannedAsset(
            libraryID: libraryID,
            path: url,
            relativePath: relativePath,
            sizeBytes: Int64(size ?? 0),
            fileModifiedAt: modified,
            kind: 2,
            formatCode: format,
            mimeType: UTType(filenameExtension: format)?.preferredMIMEType ?? "video/quicktime",
            imageFlavor: 0,
            isRaw: false,
            width: width,
            height: height,
            durationSeconds: duration.isFinite && duration >= 0 ? duration : nil,
            frameRate: frameRate,
            capturedAt: capturedAt,
            capturedAtSource: capturedAtSource
        )
    }

    // MARK: 值转换

    public enum ScanError: LocalizedError {
        case unreadable(String)
        public var errorDescription: String? {
            switch self {
            case let .unreadable(message): return message
            }
        }
    }

    static let exifFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone.current
        formatter.dateFormat = "yyyy:MM:dd HH:mm:ss"
        return formatter
    }()

    static func stringValue(_ value: Any?) -> String? {
        guard let value else { return nil }
        let text = (value as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let text, !text.isEmpty else { return nil }
        return text
    }

    static func doubleValue(_ value: Any?) -> Double? {
        if let number = value as? NSNumber { return number.doubleValue }
        if let double = value as? Double { return double }
        if let text = value as? String { return Double(text) }
        return nil
    }

    static func intValue(_ value: Any?) -> Int? {
        if let number = value as? NSNumber { return number.intValue }
        if let array = value as? [Any], let first = array.first { return intValue(first) }
        if let text = value as? String { return Int(text) }
        return nil
    }
}