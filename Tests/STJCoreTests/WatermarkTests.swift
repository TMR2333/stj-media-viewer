import AppKit
import XCTest
@testable import STJCore

final class WatermarkTests: XCTestCase {
    private func fixtureDirectory() -> URL? {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let candidates = [
            packageRoot.appendingPathComponent(".testdata"),
            URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent(".testdata"),
        ]
        return candidates.first { FileManager.default.fileExists(atPath: $0.appendingPathComponent("catalog.db").path) }
    }

    /// 纯蓝底图（CoreGraphics 绘制，核心层与测试都不再依赖 NSImage）。
    private func makeImage(width: Int = 200, height: Int = 120) -> CGImage {
        let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(CGColor(red: 0.10, green: 0.35, blue: 0.85, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()!
    }

    /// 取某个像素的 RGBA 字节（用 1×1 重绘采样，避免依赖图像内部布局）。
    private func pixel(_ image: CGImage, x: Int, y: Int) -> [UInt8] {
        let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: 4)
        defer { buffer.deallocate() }
        buffer.initialize(repeating: 0, count: 4)
        guard let context = CGContext(data: buffer, width: 1, height: 1, bitsPerComponent: 8,
                                      bytesPerRow: 4, space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            return [0, 0, 0, 0]
        }
        // y 轴：CoreGraphics 原点在左下，传入的 y 也按左下计。
        context.draw(image, in: CGRect(x: -CGFloat(x), y: -CGFloat(y),
                                       width: CGFloat(image.width), height: CGFloat(image.height)))
        return [buffer[0], buffer[1], buffer[2], buffer[3]]
    }

    func testRenderKeepsSizeAndDrawsOverlay() {
        let source = makeImage()
        let rendered = Watermarker.render(source, style: WatermarkStyle(text: "STJ 测试", position: .bottomRight, opacity: 0.9))
        let output = try! XCTUnwrap(rendered)
        XCTAssertEqual(output.width, source.width)
        XCTAssertEqual(output.height, source.height)
        var changed = 0
        for x in stride(from: 140, through: 196, by: 2) {
            for y in stride(from: 2, through: 18, by: 2) where pixel(source, x: x, y: y) != pixel(output, x: x, y: y) {
                changed += 1
            }
        }
        XCTAssertGreaterThan(changed, 3, "右下角应叠加文字像素")
        // 中心区域不应被水印影响
        XCTAssertEqual(pixel(source, x: 100, y: 60), pixel(output, x: 100, y: 60))
    }

    func testEmptyTextProducesNoOverlay() {
        let source = makeImage()
        let output = try! XCTUnwrap(Watermarker.render(source, style: WatermarkStyle(text: "   ")))
        XCTAssertEqual(pixel(source, x: 180, y: 8), pixel(output, x: 180, y: 8))
    }

    func testExportSkipsVideosAndWritesJPEG() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("watermark-\(UUID().uuidString)")
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        let fixture = try XCTUnwrap(fixtureDirectory())
        let jpeg = try XCTUnwrap(
            try FileManager.default.contentsOfDirectory(at: fixture.appendingPathComponent("media/images"),
                                                        includingPropertiesForKeys: nil)
                .filter { ["jpg", "jpeg"].contains($0.pathExtension.lowercased()) }
                .sorted { $0.lastPathComponent < $1.lastPathComponent }.first)
        let photo = Asset(id: UUID().uuidString, path: jpeg.path, fileName: jpeg.lastPathComponent,
                          kind: 1, formatCode: "jpeg", sizeBytes: 100, width: 1200, height: 800)
        let video = Asset(id: UUID().uuidString, path: jpeg.path, fileName: "clip.mov",
                          kind: 2, formatCode: "mov", sizeBytes: 100)
        let summary = try Watermarker.export(assets: [photo, video], to: directory,
                                             style: WatermarkStyle(text: "STJ"))
        XCTAssertEqual(summary.exported, 1)
        XCTAssertEqual(summary.skipped, 1)
        XCTAssertTrue(summary.failures.isEmpty)
        let files = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        XCTAssertEqual(files.count, 1)
        XCTAssertTrue(files[0].hasSuffix("_水印.jpg"))
        XCTAssertNotNil(ImageCodecs.decode(at: directory.appendingPathComponent(files[0])))
    }
}
