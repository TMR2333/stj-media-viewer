import CoreGraphics
import CoreImage
import Foundation

/// 3D LUT（.cube 文本格式），用 Core Image 的 `CIColorCube` 应用。
public struct CubeLUT: Identifiable, Sendable, Equatable {
    public var id: String { name }
    /// 文件名（不含扩展名），用于界面展示。
    public var name: String
    /// `LUT_3D_SIZE`。
    public var dimension: Int
    /// `.cube` 原始数据顺序：r 变化最快、其次 g、最后 b，每项 3 个 Float。
    public var values: [Float]

    public init(name: String, dimension: Int, values: [Float]) {
        self.name = name
        self.dimension = dimension
        self.values = values
    }

    /// 解析 .cube 文本；失败返回 nil（缺 LUT_3D_SIZE、数据条数不符等）。
    public static func parse(_ text: String, name: String = "LUT") -> CubeLUT? {
        var dimension = 0
        var values: [Float] = []
        for rawLine in text.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty, !line.hasPrefix("#") else { continue }
            let parts = line.split(whereSeparator: { $0 == " " || $0 == "\t" })
            guard let head = parts.first else { continue }
            let key = String(head).uppercased()
            if key == "LUT_3D_SIZE" {
                dimension = Int(parts.count > 1 ? String(parts[1]) : "") ?? 0
            } else if key == "TITLE" {
                continue
            } else if key == "LUT_1D_SIZE" || key == "DOMAIN_MIN" || key == "DOMAIN_MAX" {
                continue
            } else if parts.count == 3,
                      let r = Float(String(parts[0])),
                      let g = Float(String(parts[1])),
                      let b = Float(String(parts[2])) {
                values.append(r)
                values.append(g)
                values.append(b)
            }
        }
        guard dimension >= 2, values.count == dimension * dimension * dimension * 3 else { return nil }
        return CubeLUT(name: name, dimension: dimension, values: values)
    }

    public static func load(from url: URL) -> CubeLUT? {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return nil }
        return parse(text, name: url.deletingPathExtension().lastPathComponent)
    }

    /// `CIColorCube` 需要的 RGBA Float32 数据：按 r 最快、g 次之、b 最慢展开，alpha=1。
    public var cubeData: Data {
        var rgba = [Float](repeating: 1, count: dimension * dimension * dimension * 4)
        for index in 0..<(dimension * dimension * dimension) {
            rgba[index * 4 + 0] = values[index * 3 + 0]
            rgba[index * 4 + 1] = values[index * 3 + 1]
            rgba[index * 4 + 2] = values[index * 3 + 2]
        }
        return rgba.withUnsafeBufferPointer { Data(buffer: $0) }
    }

    /// 对 CIImage 应用 LUT；失败返回 nil。
    public func apply(to image: CIImage) -> CIImage? {
        guard let filter = CIFilter(name: "CIColorCube") else { return nil }
        filter.setValue(dimension, forKey: "inputCubeDimension")
        filter.setValue(cubeData, forKey: "inputCubeData")
        filter.setValue(image, forKey: kCIInputImageKey)
        return filter.outputImage
    }

    /// 恒等 LUT（测试与预览占位用）。
    public static func identity(dimension: Int = 2) -> CubeLUT {
        var values: [Float] = []
        let denominator = Float(dimension - 1)
        for b in 0..<dimension {
            for g in 0..<dimension {
                for r in 0..<dimension {
                    values.append(Float(r) / denominator)
                    values.append(Float(g) / denominator)
                    values.append(Float(b) / denominator)
                }
            }
        }
        return CubeLUT(name: "identity", dimension: dimension, values: values)
    }
}

public enum LUTDirectory {
    /// 数据目录下的 `LUTs/` 文件夹；返回已解析的 LUT，按文件名排序。
    public static func loadAll(in directory: URL?) -> [CubeLUT] {
        guard let directory else { return [] }
        let folder = directory.appendingPathComponent("LUTs", isDirectory: true)
        let urls = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        return urls
            .filter { $0.pathExtension.lowercased() == "cube" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
            .compactMap { CubeLUT.load(from: $0) }
    }
}

public enum LUTPreview {
    /// 把 LUT 应用到图像，返回渲染结果（失败返回 nil，由调用方决定回退策略）。
    /// 输入输出都用 `CGImage`，macOS / iOS 共用。
    public static func render(_ image: CGImage, lut: CubeLUT) -> CGImage? {
        let context = CIContext(options: [.useSoftwareRenderer: false])
        let size = CGSize(width: image.width, height: image.height)
        let input = CIImage(cgImage: image)
        guard let output = lut.apply(to: input) else { return nil }
        return context.createCGImage(output, from: CGRect(origin: .zero, size: size))
    }
}
