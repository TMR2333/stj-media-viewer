// 设置与图片数据跨平台交换（导入/导出）测试。
//
// 覆盖：文件格式校验、默认值省略、密钥不落盘、素材匹配（文件名 + 字节数）、
// 合并策略（只填空 / 覆盖）、合集还原、Windows 侧旧密度命名映射。

import Foundation
import XCTest
@testable import STJCore

final class SettingsExchangeTests: XCTestCase {

    // MARK: - Fixture

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
        throw XCTSkip("缺少 .testdata 样例库，跳过设置交换测试。")
    }

    /// 用样例库里的两张图片建一个干净素材库，返回可写 Catalog。
    private func makeLibrary(imageCount: Int = 2) throws -> (catalog: Catalog, root: URL) {
        let fixture = try fixtureDirectory()
        let root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stjexchange-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("media"), withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }

        let images = try FileManager.default
            .contentsOfDirectory(at: fixture.appendingPathComponent("media/images"), includingPropertiesForKeys: nil)
            .filter { ["jpg", "jpeg"].contains($0.pathExtension.lowercased()) }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
            .prefix(imageCount)
        for (index, image) in images.enumerated() {
            try FileManager.default.copyItem(at: image, to: root.appendingPathComponent("EX_\(String(format: "%04d", index + 1)).jpg"))
        }
        _ = try Importer.importFolder(root, generateThumbnails: false)
        return (try Catalog(directory: root), root)
    }

    private func document(for assets: [Asset],
                          rating: Int? = nil,
                          city: String? = nil,
                          collections: [ExchangeCollection] = []) throws -> ExchangeDocument {
        let entries = assets.map { asset in
            ExchangeAsset(fileName: asset.fileName, sizeBytes: asset.sizeBytes,
                          relativePath: asset.relativePath,
                          rating: rating, city: city)
        }
        return try SettingsExchange.makeDocument(
            settings: ExchangeSettings(themeMode: "dark", density: "large",
                                       sort: ExchangeSort(key: "rating", ascending: false),
                                       panels: ExchangePanels(sidebar: true, inspector: false),
                                       foldRawPairs: true),
            assets: entries,
            collections: collections,
            appVersion: "1.38.0",
            exportedAt: Date(timeIntervalSince1970: 1_700_000_000)
        )
    }

    // MARK: - 格式

    func testDocumentRoundTripAndValidation() throws {
        let library = try makeLibrary(imageCount: 1)
        let asset = try XCTUnwrap(try library.catalog.loadAssets(limit: 10).first)
        let original = try document(for: [asset], rating: 4, city: "上海")

        let data = try SettingsExchange.encode(original)
        let decoded = try SettingsExchange.decode(data)

        XCTAssertEqual(decoded.format, ExchangeFormat.id)
        XCTAssertEqual(decoded.formatVersion, 1)
        XCTAssertEqual(decoded.app.platform, "macos")
        XCTAssertEqual(decoded.app.version, "1.38.0")
        XCTAssertEqual(decoded.app.exportedAt, original.app.exportedAt)
        XCTAssertEqual(decoded.settings, original.settings)
        XCTAssertEqual(decoded.assets, original.assets)
        XCTAssertEqual(decoded.settings.themeMode, "dark")
        XCTAssertEqual(decoded.settings.density, "large")
        XCTAssertEqual(decoded.settings.sort?.key, "rating")
    }

    func testRejectsBadFormatAndVersion() throws {
        let badFormat = """
        {"format":"something-else","formatVersion":1,"app":{"name":"x","version":"1","platform":"macos","exportedAt":"2026"},
         "settings":{},"assets":[],"collections":[]}
        """.data(using: .utf8)!
        XCTAssertThrowsError(try SettingsExchange.decode(badFormat)) { error in
            guard case ExchangeError.badFormat = error else {
                return XCTFail("应报 badFormat，实际 \(error)")
            }
        }

        let futureVersion = """
        {"format":"stj-media-viewer/exchange","formatVersion":99,"app":{"name":"x","version":"1","platform":"macos","exportedAt":"2026"},
         "settings":{},"assets":[],"collections":[]}
        """.data(using: .utf8)!
        XCTAssertThrowsError(try SettingsExchange.decode(futureVersion)) { error in
            guard case ExchangeError.unsupportedVersion(99) = error else {
                return XCTFail("应报 unsupportedVersion，实际 \(error)")
            }
        }
    }

    // MARK: - 导出内容

    func testExportOnlyChangedFieldsAndNoSecrets() throws {
        let library = try makeLibrary(imageCount: 1)
        let asset = try XCTUnwrap(try library.catalog.loadAssets(limit: 10).first)

        // 未编辑的素材 → 没有可导出内容。
        XCTAssertFalse(ExchangeAsset(asset: asset).hasUserData)
        XCTAssertEqual(ExchangeAsset(asset: asset).rating, nil)
        XCTAssertEqual(ExchangeAsset(asset: asset).postProcess, nil)

        var edited = asset
        edited.rating = 5
        edited.city = "巴黎"
        edited.themes = ["城市夜景", "风光"]
        edited.keywords = ["埃菲尔铁塔", "夜景"]
        edited.postProcess = 3
        let payload = ExchangeAsset(asset: edited)
        XCTAssertTrue(payload.hasUserData)
        XCTAssertEqual(payload.rating, 5)
        XCTAssertEqual(payload.city, "巴黎")
        XCTAssertEqual(payload.themes, ["城市夜景", "风光"])
        XCTAssertEqual(payload.keywords, ["埃菲尔铁塔", "夜景"])
        XCTAssertEqual(payload.postProcess, 3)

        let document = try document(for: [edited], rating: 5, city: "巴黎")
        let text = String(data: try SettingsExchange.encode(document), encoding: .utf8) ?? ""
        XCTAssertTrue(text.contains("\"format\" : \"stj-media-viewer/exchange\""))
        XCTAssertTrue(text.contains("\"themeMode\" : \"dark\""))
        for secret in ["apiKey", "APIKey", "password", "Password", "protectedApiKey", "DPAPI"] {
            XCTAssertFalse(text.contains(secret), "交换文件不应包含密钥字段 \(secret)")
        }
    }

    // MARK: - 匹配与合并

    func testPlanMatchesByFileNameAndSize() throws {
        let library = try makeLibrary(imageCount: 2)
        let assets = try library.catalog.loadAssets(limit: 10)
        XCTAssertEqual(assets.count, 2)

        let matchedAsset = assets[0]
        let missingKey = ExchangeAssetKey(fileName: "NOT_IN_LIBRARY.jpg", sizeBytes: 42)
        let extension_ = try SettingsExchange.makeDocument(
            settings: ExchangeSettings(),
            assets: [ExchangeAsset(asset: matchedAsset),
                     ExchangeAsset(fileName: missingKey.fileName, sizeBytes: missingKey.sizeBytes, rating: 3)],
            appVersion: "1.38.0"
        )
        let plan = SettingsExchange.plan(document: extension_, assets: assets)
        XCTAssertEqual(plan.matchedCount, 1)
        XCTAssertEqual(plan.unmatchedCount, 1)
        XCTAssertEqual(plan.matched.first?.assetID, matchedAsset.id)
        XCTAssertEqual(plan.unmatched.first?.fileName, "NOT_IN_LIBRARY.jpg")
    }

    func testFillEmptyKeepsExistingValuesAndOverwriteReplacesThem() throws {
        let library = try makeLibrary(imageCount: 1)
        let asset = try XCTUnwrap(try library.catalog.loadAssets(limit: 10).first)

        var patch = AssetPatch()
        patch.rating = 4
        try library.catalog.update(patch: patch, ids: [asset.id])

        let document = try document(for: [asset], rating: 5, city: "东京")
        let plan = SettingsExchange.plan(document: document,
                                         assets: try library.catalog.loadAssets(limit: 10))

        // 只填空：评分保留 4，城市被填入。
        let fill = try SettingsExchange.apply(plan: plan, mode: .fillEmpty, catalog: library.catalog)
        XCTAssertEqual(fill.updatedAssets, 1)
        var reloaded = try XCTUnwrap(try library.catalog.loadAssets(limit: 10).first)
        XCTAssertEqual(reloaded.rating, 4)
        XCTAssertEqual(reloaded.city, "东京")

        // 再导入一次（只填空）→ 没有可改内容。
        let again = try SettingsExchange.apply(plan: SettingsExchange.plan(
            document: document, assets: try library.catalog.loadAssets(limit: 10)),
            mode: .fillEmpty, catalog: library.catalog)
        XCTAssertEqual(again.updatedAssets, 0)
        XCTAssertEqual(again.skippedAssets, 1)

        // 覆盖：评分改成 5。
        _ = try SettingsExchange.apply(plan: SettingsExchange.plan(
            document: document, assets: try library.catalog.loadAssets(limit: 10)),
            mode: .overwrite, catalog: library.catalog)
        reloaded = try XCTUnwrap(try library.catalog.loadAssets(limit: 10).first)
        XCTAssertEqual(reloaded.rating, 5)
    }

    func testApplyRestoresCollections() throws {
        let library = try makeLibrary(imageCount: 2)
        let assets = try library.catalog.loadAssets(limit: 10)
        let keys = assets.map { ExchangeAssetKey(asset: $0) }
        let collection = ExchangeCollection(name: "跨平台合集", assets: keys)

        let plan = SettingsExchange.plan(
            document: try document(for: assets, collections: [collection]),
            assets: assets
        )
        XCTAssertEqual(plan.collections.count, 1)

        let summary = try SettingsExchange.apply(plan: plan, mode: .fillEmpty, catalog: library.catalog)
        XCTAssertEqual(summary.createdCollections, 1)
        XCTAssertEqual(summary.addedCollectionMembers, 2)

        let collections = try library.catalog.collections()
        XCTAssertEqual(collections.first?.name, "跨平台合集")
        XCTAssertEqual(collections.first?.count, 2)

        // 重复导入同一文件不会重复建合集。
        let second = try SettingsExchange.apply(plan: plan, mode: .fillEmpty, catalog: library.catalog)
        XCTAssertEqual(second.createdCollections, 0)
        XCTAssertEqual(try library.catalog.collections().count, 1)
    }

    // MARK: - 设置映射

    func testLegacyDensityNamesFromWindows() {
        XCTAssertEqual(LibraryDensity(legacyName: "compact"), .small)
        XCTAssertEqual(LibraryDensity(legacyName: "comfortable"), .medium)
        XCTAssertEqual(LibraryDensity(legacyName: "spacious"), .large)
        XCTAssertEqual(LibraryDensity(legacyName: "list"), .list)
        XCTAssertNil(LibraryDensity(legacyName: "unknown"))

        // 中立命名本身可直接解析。
        XCTAssertEqual(LibraryDensity(rawValue: "small"), .small)
        XCTAssertEqual(AppearanceMode(rawValue: "dark"), .dark)
        XCTAssertEqual(SortKey(rawValue: "rating"), .rating)
    }
}