import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// 跨平台图像编解码：对外只暴露 `CGImage` / `Data`，不依赖 AppKit 或 UIKit。
/// macOS 侧再包成 `NSImage`、iOS 侧包成 `UIImage`（如果需要），核心层保持纯净。
public enum ImageCodecs {
    /// 从文件解码，可限制最长边（用于缩略图与画廊预载）。
    public static func decode(at url: URL, maxPixelSize: Int? = nil) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        return decode(source: source, maxPixelSize: maxPixelSize)
    }

    public static func decode(_ data: Data, maxPixelSize: Int? = nil) -> CGImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        return decode(source: source, maxPixelSize: maxPixelSize)
    }

    public static func decode(source: CGImageSource, maxPixelSize: Int? = nil) -> CGImage? {
        guard CGImageSourceGetCount(source) > 0 else { return nil }
        if let maxPixelSize {
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
                kCGImageSourceShouldCacheImmediately: true,
            ]
            return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
        }
        return CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary)
    }

    /// 编码为 JPEG（缩略图、AI 预览、水印导出共用）。
    public static func jpegData(_ image: CGImage, quality: CGFloat = 0.82) -> Data? {
        encode(image, type: .jpeg, quality: quality)
    }

    public static func pngData(_ image: CGImage) -> Data? {
        encode(image, type: .png, quality: nil)
    }

    public static func encode(_ image: CGImage, type: UTType, quality: CGFloat?) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data as CFMutableData, type.identifier as CFString, 1, nil) else {
            return nil
        }
        var properties: [CFString: Any] = [:]
        if let quality { properties[kCGImageDestinationLossyCompressionQuality] = quality }
        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return data as Data
    }

    /// 原子写入（`.atomic` 会先写临时文件再替换，iOS 与 macOS 行为一致）。
    public static func writeJPEG(_ image: CGImage, to url: URL, quality: CGFloat = 0.92) throws {
        guard let data = jpegData(image, quality: quality) else { throw ImageCodecError.encodeFailed }
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }
}

public enum ImageCodecError: LocalizedError {
    case encodeFailed

    public var errorDescription: String? {
        switch self {
        case .encodeFailed: return "图像编码失败"
        }
    }
}
