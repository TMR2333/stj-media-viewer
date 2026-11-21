import AppKit
import CoreImage
import XCTest
@testable import STJCore

final class LUTTests: XCTestCase {
    private func sampleCube(dimension: Int = 2, transform: (Float, Float, Float) -> (Float, Float, Float)) -> String {
        var lines = ["TITLE \"test\"", "LUT_3D_SIZE \(dimension)"]
        let denominator = Float(dimension - 1)
        for b in 0..<dimension {
            for g in 0..<dimension {
                for r in 0..<dimension {
                    let (rr, gg, bb) = transform(Float(r) / denominator, Float(g) / denominator, Float(b) / denominator)
                    lines.append(String(format: "%.4f %.4f %.4f", rr, gg, bb))
                }
            }
        }
        return lines.joined(separator: "\n") + "\n"
    }

    func testParseIdentityAndCubeDataShape() {
        let text = sampleCube { r, g, b in (r, g, b) }
        let lut = CubeLUT.parse(text, name: "identity")
        XCTAssertNotNil(lut)
        XCTAssertEqual(lut?.dimension, 2)
        XCTAssertEqual(lut?.values.count, 2 * 2 * 2 * 3)
        XCTAssertEqual(lut?.cubeData.count, 2 * 2 * 2 * 4 * MemoryLayout<Float>.size)
    }

    func testParseRejectsIncompleteData() {
        let truncated = "LUT_3D_SIZE 2\n0 0 0\n1 0 0\n"
        XCTAssertNil(CubeLUT.parse(truncated))
        XCTAssertNil(CubeLUT.parse(""))
    }

    func testLUTChangesPixelColor() {
        // 把所有颜色压成一半亮度：灰度中点应明显变暗。
        let text = sampleCube { r, g, b in (r * 0.5, g * 0.5, b * 0.5) }
        let lut = CubeLUT.parse(text, name: "dim")!
        let input = CIImage(color: CIColor(red: 0.8, green: 0.6, blue: 0.4)).cropped(to: CGRect(x: 0, y: 0, width: 4, height: 4))
        let output = lut.apply(to: input)
        XCTAssertNotNil(output)
        let context = CIContext(options: [.workingColorSpace: NSNull()])
        guard let output, let cgImage = context.createCGImage(output, from: input.extent) else {
            return XCTFail("无法渲染 LUT 输出")
        }
        let bitmap = NSBitmapImageRep(cgImage: cgImage)
        guard let color = bitmap.colorAt(x: 1, y: 1) else { return XCTFail("取色失败") }
        XCTAssertLessThan(color.redComponent, 0.65, "LUT 应降低红色分量")
        XCTAssertLessThan(color.greenComponent, 0.45, "LUT 应降低绿色分量")
    }

    func testIdentityLUTKeepsColor() {
        let lut = CubeLUT.identity(dimension: 2)
        let input = CIImage(color: CIColor(red: 0.25, green: 0.5, blue: 0.75)).cropped(to: CGRect(x: 0, y: 0, width: 2, height: 2))
        let context = CIContext(options: [.workingColorSpace: NSNull()])
        guard let output = lut.apply(to: input), let cgImage = context.createCGImage(output, from: input.extent) else {
            return XCTFail("无法渲染")
        }
        let bitmap = NSBitmapImageRep(cgImage: cgImage)
        guard let color = bitmap.colorAt(x: 1, y: 1) else { return XCTFail("取色失败") }
        XCTAssertEqual(color.redComponent, 0.25, accuracy: 0.02)
        XCTAssertEqual(color.greenComponent, 0.5, accuracy: 0.02)
        XCTAssertEqual(color.blueComponent, 0.75, accuracy: 0.02)
    }

    func testLUTDirectoryLoadsCubeFiles() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("lut-\(UUID().uuidString)")
        let lutFolder = folder.appendingPathComponent("LUTs")
        try FileManager.default.createDirectory(at: lutFolder, withIntermediateDirectories: true)
        try sampleCube { r, g, b in (r, g, b) }.write(to: lutFolder.appendingPathComponent("aaa.cube"), atomically: true, encoding: .utf8)
        try sampleCube { r, g, b in (1 - r, g, b) }.write(to: lutFolder.appendingPathComponent("zzz.CUBE"), atomically: true, encoding: .utf8)
        try "not a lut".write(to: lutFolder.appendingPathComponent("readme.txt"), atomically: true, encoding: .utf8)
        addTeardownBlock { try? FileManager.default.removeItem(at: folder) }

        let luts = LUTDirectory.loadAll(in: folder)
        XCTAssertEqual(luts.map(\.name), ["aaa", "zzz"])
    }
}
