import AppKit
import Foundation

/// 水印样式（导出时叠加在图片右下角等位置）。
public struct WatermarkStyle: Equatable, Sendable {
    public enum Position: String, CaseIterable, Identifiable, Sendable {
        case bottomRight, bottomLeft, topRight, topLeft, bottomCenter
        public var id: String { rawValue }
        public var title: String {
            switch self {
            case .bottomRight: return "右下角"
            case .bottomLeft: return "左下角"
            case .topRight: return "右上角"
            case .topLeft: return "左上角"
            case .bottomCenter: return "底部居中"
            }
        }
    }

    public var text: String
    public var position: Position
    /// 不透明度 0...1。
    public var opacity: Double
    /// 字号相对图片短边的比例（0.01...0.2）。
    public var relativeFontSize: Double
    public var includesCameraModel: Bool

    public init(text: String = "STJ Media Viewer",
                position: Position = .bottomRight,
                opacity: Double = 0.75,
                relativeFontSize: Double = 0.028,
                includesCameraModel: Bool = false) {
        self.text = text
        self.position = position
        self.opacity = opacity
        self.relativeFontSize = relativeFontSize
        self.includesCameraModel = includesCameraModel
    }

    public static let disabled = WatermarkStyle(text: "")
    public var isEnabled: Bool { !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
}

public struct WatermarkExportSummary: Sendable {
    public var exported: Int
    public var skipped: Int
    public var failures: [String]
    public var directory: URL
    public var duration: TimeInterval

    public var descriptionText: String {
        var parts = ["导出 \(exported) 张"]
        if skipped > 0 { parts.append("跳过 \(skipped)") }
        if !failures.isEmpty { parts.append("失败 \(failures.count)") }
        return parts.joined(separator: " · ") + "（\(String(format: "%.1f", duration)) 秒）"
    }
}

public enum WatermarkError: Error, LocalizedError {
    case unreadableImage(String)

    public var errorDescription: String? {
        switch self {
        case .unreadableImage(let name): return "无法读取图像：\(name)"
        }
    }
}

/// 导出带水印的 JPEG。
public enum Watermarker {
    public static func render(_ image: NSImage, style: WatermarkStyle, asset: Asset? = nil) -> NSImage {
        let size = image.size
        guard size.width > 1, size.height > 1 else { return image }
        let composed = NSImage(size: size)
        composed.lockFocus()
        image.draw(in: NSRect(origin: .zero, size: size))

        var lines: [String] = [style.text]
        if style.includesCameraModel, let model = asset?.cameraModel, !model.isEmpty {
            lines.append(model)
        }
        lines = lines.filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        if !lines.isEmpty {
            let shortEdge = min(size.width, size.height)
            let fontSize = max(9, shortEdge * style.relativeFontSize)
            let shadow = NSShadow()
            shadow.shadowColor = NSColor.black.withAlphaComponent(0.55)
            shadow.shadowBlurRadius = fontSize * 0.25
            shadow.shadowOffset = NSSize(width: 0, height: -fontSize * 0.06)

            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: fontSize, weight: .semibold),
                .foregroundColor: NSColor.white.withAlphaComponent(style.opacity),
                .shadow: shadow,
            ]
            let texts = lines.map { NSAttributedString(string: $0, attributes: attributes) }
            let widths = texts.map { $0.size().width }
            let heights = texts.map { $0.size().height }
            let margin = shortEdge * 0.03
            let blockWidth = widths.max() ?? 0
            let blockHeight = heights.reduce(0, +) + fontSize * 0.15 * Double(max(0, texts.count - 1))
            var origin: NSPoint
            switch style.position {
            case .bottomRight: origin = NSPoint(x: size.width - margin - blockWidth, y: margin)
            case .bottomLeft: origin = NSPoint(x: margin, y: margin)
            case .topRight: origin = NSPoint(x: size.width - margin - blockWidth, y: size.height - margin - blockHeight)
            case .topLeft: origin = NSPoint(x: margin, y: size.height - margin - blockHeight)
            case .bottomCenter: origin = NSPoint(x: (size.width - blockWidth) / 2, y: margin)
            }
            var y = origin.y + blockHeight
            for (index, text) in texts.enumerated() {
                let h = heights[index]
                y -= h
                var x = origin.x
                if style.position == .bottomRight || style.position == .topRight {
                    x = size.width - margin - widths[index]
                } else if style.position == .bottomCenter {
                    x = (size.width - widths[index]) / 2
                }
                text.draw(at: NSPoint(x: x, y: y))
                y -= fontSize * 0.15
            }
        }
        composed.unlockFocus()
        return composed
    }

    /// 从原图（非缩略图）加载并叠加水印，生成 JPEG data。
    public static func jpegData(for asset: Asset, style: WatermarkStyle, quality: CGFloat = 0.92) -> Data? {
        guard let image = ImageLoader.previewImage(at: asset.fileURL, maxPixelSize: 6000) else { return nil }
        let composed = render(image, style: style, asset: asset)
        guard let tiff = composed.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff) else { return nil }
        return bitmap.representation(using: .jpeg, properties: [.compressionFactor: quality])
    }

    /// 把选中素材导出到目录，文件名为 `原文件名_水印.jpg`，重名自动加序号。
    public static func export(assets: [Asset],
                              to directory: URL,
                              style: WatermarkStyle,
                              fileManager: FileManager = .default) throws -> WatermarkExportSummary {
        let started = Date()
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        var exported = 0, skipped = 0
        var failures: [String] = []
        for asset in assets {
            guard asset.kind != 2 else { skipped += 1; continue }
            guard let data = jpegData(for: asset, style: style) else {
                failures.append("\(asset.fileName)：无法解码")
                continue
            }
            let base = asset.fileURL.deletingPathExtension().lastPathComponent
            var candidate = directory.appendingPathComponent("\(base)_水印.jpg")
            var index = 2
            while fileManager.fileExists(atPath: candidate.path) {
                candidate = directory.appendingPathComponent("\(base)_水印-\(index).jpg")
                index += 1
                if index > 5000 { break }
            }
            do {
                try data.write(to: candidate, options: .atomic)
                exported += 1
            } catch {
                failures.append("\(asset.fileName)：\(error.localizedDescription)")
            }
        }
        return WatermarkExportSummary(exported: exported, skipped: skipped, failures: failures,
                                      directory: directory, duration: Date().timeIntervalSince(started))
    }
}
