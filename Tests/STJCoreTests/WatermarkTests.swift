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

    private func image(width: CGFloat = 200, height: CGFloat = 120) -> NSImage {
        let image = NSImage(size: NSSize(width: width, height: height))
        image.lockFocus()
        NSColor(calibratedRed: 0.10, green: 0.35, blue: 0.85, alpha: 1).setFill()
        NSRect(x: 0, y: 0, width: width, height: height).fill()
        image.unlockFocus()
        return image
    }

    private func pixel(_ image: NSImage, x: Int, y: Int) -> NSColor? {
        guard let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff) else { return nil }
        let px = x * bitmap.pixelsWide / Int(image.size.width)
        // NSBitmapImageRep 的 y 轴从顶部开始，而绘图坐标从底部开始
        let py = bitmap.pixelsHigh - 1 - y * bitmap.pixelsHigh / Int(image.size.height)
        return bitmap.colorAt(x: min(max(0, px), bitmap.pixelsWide - 1),
                              y: min(max(0, py), bitmap.pixelsHigh - 1))
    }

    func testRenderKeepsSizeAndDrawsOverlay() {
        let source = image()
        let rendered = Watermarker.render(source, style: WatermarkStyle(text: "STJ 测试", position: .bottomRight, opacity: 0.9))
        XCTAssertEqual(rendered.size, source.size)
        var changed = 0
        for x in stride(from: 140, through: 196, by: 2) {
            for y in stride(from: 2, through: 18, by: 2) where pixel(source, x: x, y: y) != pixel(rendered, x: x, y: y) {
                changed += 1
            }
        }
        XCTAssertGreaterThan(changed, 3, "右下角应叠加文字像素")
        // 中心区域不应被水印影响
        XCTAssertEqual(pixel(source, x: 100, y: 60), pixel(rendered, x: 100, y: 60))
    }

    func testEmptyTextProducesNoOverlay() {
        let source = image()
        let rendered = Watermarker.render(source, style: WatermarkStyle(text: "   "))
        XCTAssertEqual(pixel(source, x: 180, y: 8), pixel(rendered, x: 180, y: 8))
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
        XCTAssertNotNil(NSImage(contentsOfFile: directory.appendingPathComponent(files[0]).path))
    }
}
