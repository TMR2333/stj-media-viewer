import XCTest
@testable import STJCore

/// 目录标签页（工作区库）相关测试：素材目录只读、按库隔离过滤。
final class WorkspaceTests: XCTestCase {

    private func fixtureDirectory() throws -> URL {
        let fileURL = URL(fileURLWithPath: #filePath)
        let packageRoot = fileURL
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let candidates = [
            packageRoot.appendingPathComponent(".testdata"),
            URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent(".testdata"),
        ]
        for candidate in candidates where FileManager.default.fileExists(
            atPath: candidate.appendingPathComponent("catalog.db").path
        ) {
            return candidate.resolvingSymlinksInPath().standardizedFileURL
        }
        throw XCTSkip("缺少 .testdata 样例库，跳过工作区测试。")
    }

    private func makeFolder(_ name: String, imageCount: Int) throws -> URL {
        let fixture = try fixtureDirectory()
        let folder = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stjws-\(name)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: folder) }
        let images = try FileManager.default
            .contentsOfDirectory(at: fixture.appendingPathComponent("media/images"),
                                 includingPropertiesForKeys: nil)
            .filter { ["jpg", "jpeg"].contains($0.pathExtension.lowercased()) }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        for index in 0..<min(imageCount, images.count) {
            let target = folder.appendingPathComponent(String(format: "%@_%04d.jpg", name, index))
            try FileManager.default.copyItem(at: images[index], to: target)
        }
        return folder.resolvingSymlinksInPath().standardizedFileURL
    }

    /// 导入只在工作区库与工作区缓存里落文件，素材目录保持干净。
    func testImportWritesNothingIntoMediaFolder() throws {
        let folder = try makeFolder("clean", imageCount: 3)
        let workspace = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stjws-db-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: workspace) }
        let databaseURL = workspace.appendingPathComponent("workspace.sqlite")
        let cacheURL = workspace.appendingPathComponent("Cache/thumbs")

        let summary = try Importer.importFolder(folder,
                                                databaseURL: databaseURL,
                                                cacheDirectory: cacheURL,
                                                libraryName: "clean")
        XCTAssertEqual(summary.added, 3)
        XCTAssertTrue(FileManager.default.fileExists(atPath: databaseURL.path))
        XCTAssertGreaterThan(summary.thumbnails, 0)

        let entries = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        XCTAssertFalse(entries.contains("catalog.db"), "素材目录里不应出现 catalog.db：\(entries)")
        XCTAssertFalse(entries.contains("Cache"), "素材目录里不应出现 Cache：\(entries)")

        let catalog = try Catalog(databaseURL: databaseURL)
        let libraryID = try XCTUnwrap(catalog.libraryID(forDirectory: folder))
        var filter = AssetFilter()
        filter.libraryIDs = [libraryID]
        XCTAssertEqual(try catalog.count(filter: filter), 3)
        XCTAssertEqual(try catalog.loadAssets(filter: filter).count, 3)
    }

    /// 多个目录共用一个工作区库，过滤必须互相隔离。
    func testLibrariesAreIsolatedByFilter() throws {
        let first = try makeFolder("alpha", imageCount: 2)
        let second = try makeFolder("beta", imageCount: 4)
        let workspace = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stjws-multi-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: workspace) }
        let databaseURL = workspace.appendingPathComponent("workspace.sqlite")
        let cacheURL = workspace.appendingPathComponent("Cache/thumbs")

        _ = try Importer.importFolder(first, databaseURL: databaseURL, cacheDirectory: cacheURL, libraryName: "alpha")
        _ = try Importer.importFolder(second, databaseURL: databaseURL, cacheDirectory: cacheURL, libraryName: "beta")

        let catalog = try Catalog(databaseURL: databaseURL)
        let alphaID = try XCTUnwrap(catalog.libraryID(forDirectory: first))
        let betaID = try XCTUnwrap(catalog.libraryID(forDirectory: second))
        XCTAssertNotEqual(alphaID, betaID)

        var filter = AssetFilter()
        filter.libraryIDs = [alphaID]
        XCTAssertEqual(try catalog.count(filter: filter), 2)
        let alphaAssets = try catalog.loadAssets(filter: filter)
        XCTAssertTrue(alphaAssets.allSatisfy { $0.path.hasPrefix(first.path) })

        filter.libraryIDs = [betaID]
        XCTAssertEqual(try catalog.count(filter: filter), 4)

        filter.libraryIDs = []
        XCTAssertEqual(try catalog.count(filter: filter), 6, "不限定库时应能读到全部 6 条")
    }

    /// 规范路径：/tmp 与 /private/tmp 应解析到同一个库 ID。
    func testLibraryIDResolvesSymlinkedPaths() throws {
        let folder = try makeFolder("sym", imageCount: 1)
        let workspace = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stjws-sym-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: workspace) }
        let databaseURL = workspace.appendingPathComponent("workspace.sqlite")
        _ = try Importer.importFolder(folder, databaseURL: databaseURL, libraryName: "sym")

        let catalog = try Catalog(databaseURL: databaseURL)
        let resolvedID = try XCTUnwrap(catalog.libraryID(forDirectory: folder))
        // /tmp 是 /private/tmp 的符号链接：用未解析路径也应命中同一个库。
        let unresolved = URL(fileURLWithPath: folder.path.replacingOccurrences(of: "/private", with: ""))
        XCTAssertEqual(try catalog.libraryID(forDirectory: unresolved), resolvedID)
    }
}
