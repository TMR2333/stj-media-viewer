import Foundation
import XCTest
@testable import STJCore

final class LibraryBackupTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("backup-\(UUID().uuidString)", isDirectory: true)
        try makeLibrary()
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private func makeLibrary() throws {
        let library = root.appendingPathComponent("library", isDirectory: true)
        let thumbs = library.appendingPathComponent("Cache/thumbs", isDirectory: true)
        try FileManager.default.createDirectory(at: thumbs, withIntermediateDirectories: true)
        try Data("catalog-v1".utf8).write(to: library.appendingPathComponent("catalog.db"))
        try Data([0xFF, 0xD8, 0xFF, 0xD9]).write(to: thumbs.appendingPathComponent("thumb_a.jpg"))
        try Data([0xFF, 0xD8, 0xFF]).write(to: thumbs.appendingPathComponent("thumb_b.jpg"))
    }

    private var library: URL { root.appendingPathComponent("library") }
    private var destination: URL { root.appendingPathComponent("backups") }

    func testCreateBackupCopiesDatabaseThumbsAndInfo() throws {
        let summary = try LibraryBackup.create(from: library, destination: destination, appVersion: "1.34.0")
        XCTAssertTrue(FileManager.default.fileExists(atPath: summary.destination.appendingPathComponent("catalog.db").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: summary.destination.appendingPathComponent("Cache/thumbs/thumb_a.jpg").path))
        XCTAssertEqual(summary.info.fileCount, 3)
        XCTAssertEqual(summary.info.appVersion, "1.34.0")
        let info = LibraryBackup.info(in: summary.destination)
        XCTAssertEqual(info?.sourcePath, library.path)
        XCTAssertEqual(info?.fileCount, 3)
        XCTAssertGreaterThan(summary.info.byteCount, 0)
    }

    func testRestoreReplacesDatabaseAndKeepsPreviousCopy() throws {
        let summary = try LibraryBackup.create(from: library, destination: destination)
        // 修改库内容，验证恢复后回到备份时状态
        try Data("catalog-v2-modified".utf8).write(to: library.appendingPathComponent("catalog.db"))
        let restored = try LibraryBackup.restore(from: summary.destination, to: library)
        XCTAssertTrue(restored.databaseReplaced)
        let current = try Data(contentsOf: library.appendingPathComponent("catalog.db"))
        XCTAssertEqual(String(decoding: current, as: UTF8.self), "catalog-v1")
        let previous = try XCTUnwrap(restored.previousDatabaseBackup)
        XCTAssertTrue(FileManager.default.fileExists(atPath: previous.path))
        XCTAssertEqual(FileManager.default.fileExists(atPath: library.appendingPathComponent("Cache/thumbs/thumb_b.jpg").path), true)
    }

    func testInvalidBackupThrows() throws {
        let empty = root.appendingPathComponent("empty", isDirectory: true)
        try FileManager.default.createDirectory(at: empty, withIntermediateDirectories: true)
        XCTAssertThrowsError(try LibraryBackup.restore(from: empty, to: library))
    }

    func testMissingCatalogThrows() throws {
        let emptyLibrary = root.appendingPathComponent("emptylib", isDirectory: true)
        try FileManager.default.createDirectory(at: emptyLibrary, withIntermediateDirectories: true)
        XCTAssertThrowsError(try LibraryBackup.create(from: emptyLibrary, destination: destination))
    }
}
