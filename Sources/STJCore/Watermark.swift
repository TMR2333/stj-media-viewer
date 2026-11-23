import CoreGraphics
import CoreText
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
    /// 用 Core Graphics + Core Text 在图像上叠加文字水印，返回新图。
    /// 只用 CoreGraphics/CoreText，macOS 与 iOS 共用同一份实现。
    public static func render(_ image: CGImage, style: WatermarkStyle, asset: Asset? = nil) -> CGImage? {
        let width = image.width
        let height = image.height
        guard width > 1, height > 1 else { return nil }
        let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB()
        guard let context = CGContext(data: nil,
                                      width: width,
                                      height: height,
                                      bitsPerComponent: 8,
                                      bytesPerRow: 0,
                                      space: colorSpace,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            return nil
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))

        var lines: [String] = [style.text]
        if style.includesCameraModel, let model = asset?.cameraModel, !model.isEmpty {
            lines.append(model)
        }
        lines = lines.filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        guard !lines.isEmpty else { return context.makeImage() }

        let shortEdge = Double(min(width, height))
        let fontSize = max(9, shortEdge * style.relativeFontSize)
        let font = CTFontCreateWithName("HelveticaNeue-Medium" as CFString, fontSize, nil)
        let margin = shortEdge * 0.03
        let lineGap = fontSize * 0.15
        let alpha = style.opacity

        var lineWidths: [Double] = []
        var lineHeights: [Double] = []
        var ctLines: [CTLine] = []
        for line in lines {
            let attributes: [NSAttributedString.Key: Any] = [
                kCTFontAttributeName as NSAttributedString.Key: font,
                kCTForegroundColorAttributeName as NSAttributedString.Key: CGColor(red: 1, green: 1, blue: 1, alpha: alpha),
            ]
            let attributed = NSAttributedString(string: line, attributes: attributes)
            let ctLine = CTLineCreateWithAttributedString(attributed)
            var ascent: CGFloat = 0
            var descent: CGFloat = 0
            var leading: CGFloat = 0
            let lineWidth = CTLineGetTypographicBounds(ctLine, &ascent, &descent, &leading)
            lineWidths.append(Double(lineWidth))
            lineHeights.append(Double(ascent + descent))
            ctLines.append(ctLine)
        }

        let blockWidth = lineWidths.max() ?? 0
        let blockHeight = lineHeights.reduce(0, +) + lineGap * Double(max(0, ctLines.count - 1))
        var originX: Double
        var originY: Double
        switch style.position {
        case .bottomRight:
            originX = Double(width) - margin - blockWidth
            originY = margin
        case .bottomLeft:
            originX = margin
            originY = margin
        case .topRight:
            originX = Double(width) - margin - blockWidth
            originY = Double(height) - margin - blockHeight
        case .topLeft:
            originX = margin
            originY = Double(height) - margin - blockHeight
        case .bottomCenter:
            originX = (Double(width) - blockWidth) / 2
            originY = margin
        }

        context.saveGState()
        context.setShadow(offset: CGSize(width: 0, height: -fontSize * 0.06),
                          blur: CGFloat(fontSize * 0.25),
                          color: CGColor(red: 0, green: 0, blue: 0, alpha: 0.55))
        var cursorY = originY + blockHeight
        for (index, ctLine) in ctLines.enumerated() {
            let lineHeight = lineHeights[index]
            let x: Double
            switch style.position {
            case .bottomRight, .topRight:
                x = Double(width) - margin - lineWidths[index]
            case .bottomCenter:
                x = (Double(width) - lineWidths[index]) / 2
            default:
                x = originX
            }
            cursorY -= lineHeight
            context.textPosition = CGPoint(x: x, y: cursorY + Double(CTFontGetDescent(font)))
            CTLineDraw(ctLine, context)
            cursorY -= Double(lineGap)
        }
        context.restoreGState()
        return context.makeImage()
    }

    /// 从原图（非缩略图）加载并叠加水印，生成 JPEG data。
    public static func jpegData(for asset: Asset, style: WatermarkStyle, quality: CGFloat = 0.92) -> Data? {
        guard let image = ImageLoader.previewImage(at: asset.fileURL, maxPixelSize: 6000),
              let composed = render(image, style: style, asset: asset) else { return nil }
        return ImageCodecs.jpegData(composed, quality: quality)
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
