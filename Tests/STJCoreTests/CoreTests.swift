// STJCore 核心业务语义测试。
//
// 覆盖：schema、kind/年份/城市/主题/未填写筛选、is_pick 三态、评分与排序、
// facets、批量更新写回、合集 CRUD、合并组展开、XMP 包、缩略图键与缓存命中。
//
// 写库类测试一律把 .testdata/catalog.db 复制到临时目录再操作，绝不动 fixture；
// 若 .testdata 不存在（例如刚 clone 未生成样例库），用 XCTSkip 跳过并打印提示。

import Foundation
import XCTest
@testable import STJCore

final class CoreTests: XCTestCase {

    // MARK: - Fixture

    /// 样例库句柄：只读访问 + 临时副本写操作 + 临时目录回收。
    private final class Fixture {
        let directory: URL
        let catalog: Catalog
        private var temporaryDirectories: [URL] = []

        init(directory: URL) throws {
            self.directory = directory.resolvingSymlinksInPath().standardizedFileURL
            self.catalog = try Catalog(directory: self.directory)
        }

        var databaseURL: URL { directory.appendingPathComponent("catalog.db") }
        var thumbsURL: URL { directory.appendingPathComponent("Cache/thumbs") }

        /// 把 catalog.db 复制到临时目录，返回可安全写库的 Catalog。
        func temporaryCatalog() throws -> (catalog: Catalog, directory: URL) {
            let root = URL(fileURLWithPath: NSTemporaryDirectory())
                .appendingPathComponent("stjcoretests-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            try FileManager.default.copyItem(at: databaseURL, to: root.appendingPathComponent("catalog.db"))
            temporaryDirectories.append(root)
            return (try Catalog(directory: root), root)
        }

        func cleanup() {
            for url in temporaryDirectories {
                try? FileManager.default.removeItem(at: url)
            }
            temporaryDirectories.removeAll()
        }

        func readOnlyDatabase() throws -> Database {
            try Database(path: databaseURL.path, readOnly: true)
        }

        /// 默认视图（折叠合并组）+ 组内非封面成员 = 样例库全部 28 行。
        func allAssets() throws -> [Asset] {
            var assets = try catalog.loadAssets(limit: 5_000)
            let visible = Set(assets.map(\.id))
            for group in try catalog.mergeGroups() {
                for member in try catalog.groupMembers(groupID: group.id) where !visible.contains(member.id) {
                    assets.append(member)
                }
            }
            return assets
        }

        func asset(search: String) throws -> Asset? {
            var filter = AssetFilter()
            filter.searchText = search
            return try catalog.loadAssets(filter: filter, limit: 5).first
        }
    }

    private static let coverOnlyWhere = " WHERE (is_group_cover = 1 OR group_id IS NULL OR group_id = '')"

    private static func candidateFixtureDirectories() -> [URL] {
        // #filePath = <包根>/Tests/STJCoreTests/CoreTests.swift
        let fileURL = URL(fileURLWithPath: #filePath)
        let packageRoot = fileURL
            .deletingLastPathComponent()   // Tests/STJCoreTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // 包根
        let workingDirectory = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        return [
            packageRoot.appendingPathComponent(".testdata"),
            workingDirectory.appendingPathComponent(".testdata"),
            workingDirectory.appendingPathComponent(".testdata"),
        ]
    }

    private func makeFixture() throws -> Fixture {
        for directory in Self.candidateFixtureDirectories() {
            let database = directory.appendingPathComponent("catalog.db")
            if FileManager.default.fileExists(atPath: database.path) {
                return try Fixture(directory: directory)
            }
        }
        throw XCTSkip("""
        缺少 .testdata 样例库，跳过 STJCore 业务语义测试。
        提示：在 macos/ 目录执行 python3 scripts/make-sample-library.py --data-dir .testdata
        """)
    }

    private func scalar(_ database: Database, _ sql: String, _ binds: [Any?] = []) throws -> Int {
        try database.scalarInt(sql, binds: binds)
    }

    // MARK: - ① schema 与默认可见性

    func testFixtureSchemaAndDefaultVisibility() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let database = try fixture.readOnlyDatabase()

        let columns = try database.tableColumns("assets")
        for column in ["id", "full_path", "kind", "captured_day", "city", "themes_json",
                       "theme", "is_pick", "rating", "color_space", "quick_hash",
                       "thumb_key", "group_id", "is_group_cover", "lut_id", "companions_json"] {
            XCTAssertTrue(columns.contains(column), "assets 缺少列：\(column)")
        }
        XCTAssertGreaterThanOrEqual(columns.count, 77, "assets 列数过少，schema 未补齐")

        let journalMode = try database.query("PRAGMA journal_mode;") { $0.string(0) ?? "" }.first ?? ""
        XCTAssertEqual(journalMode.lowercased(), "delete", "样例库必须用 journal_mode=delete 才能被 app 直接打开")

        XCTAssertEqual(try scalar(database, "SELECT count(*) FROM assets;"), 28, "样例库原始行数")
        XCTAssertEqual(try fixture.catalog.count(), 25, "默认视图（折叠合并组）可见行数")
        XCTAssertEqual(try scalar(database, "SELECT count(*) FROM assets WHERE group_id IS NOT NULL AND group_id <> '' AND is_group_cover = 0;"),
                       3, "被合并组折叠隐藏的非封面成员数")

        // app 打开时会调用这些 API，必须都不抛错。
        XCTAssertEqual(try fixture.catalog.libraries().count, 1)
        XCTAssertEqual(try fixture.catalog.collections().count, 3)
        XCTAssertEqual(try fixture.catalog.mergeGroups().count, 2)
        XCTAssertNoThrow(try fixture.catalog.loadFacets())
    }

    // MARK: - ② kind 筛选

    func testKindFilterSeparatesVideosAndImages() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }

        var videoFilter = AssetFilter()
        videoFilter.kinds = [2]
        let videos = try fixture.catalog.loadAssets(filter: videoFilter, limit: 100)
        XCTAssertEqual(videos.count, 3)
        XCTAssertTrue(videos.allSatisfy { $0.kind == 2 && $0.isVideo })

        var imageFilter = AssetFilter()
        imageFilter.kinds = [1]
        let images = try fixture.catalog.loadAssets(filter: imageFilter, limit: 100)
        XCTAssertTrue(images.allSatisfy { $0.kind == 1 && !$0.isVideo })
        XCTAssertEqual(images.count + videos.count, try fixture.catalog.count(), "图片 + 视频应等于默认可见行数")
        XCTAssertEqual(try fixture.catalog.count(filter: videoFilter), videos.count)
    }

    // MARK: - ③ 年份维度

    func testYearDimensionUsesCapturedDay() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let database = try fixture.readOnlyDatabase()

        var filter = AssetFilter()
        filter.years = ["2025"]
        let assets = try fixture.catalog.loadAssets(filter: filter, limit: 1_000)
        let expected = try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND substr(captured_day, 1, 4) = '2025';")
        XCTAssertEqual(assets.count, expected)
        XCTAssertGreaterThan(assets.count, 0)
        XCTAssertTrue(assets.allSatisfy { ($0.capturedDay ?? "").hasPrefix("2025") })

        let facets = try fixture.catalog.loadFacets()
        let years = Set(facets.years.map(\.value))
        XCTAssertTrue(years.isSuperset(of: ["2024", "2025", "2026"]), "facets 年份：\(years.sorted())")
        XCTAssertEqual(facets.years.first { $0.value == "2025" }?.count, expected, "facets 2025 计数")
        // 样例库每行都有 captured_day，所以年份未填写计数应为 0（facets 里不出现该 key）。
        XCTAssertEqual(try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND (captured_day IS NULL OR captured_day = '');"),
                       0)
        XCTAssertEqual(facets.unsetCounts[.year] ?? 0, 0, "facets 未填写年份计数")
    }

    // MARK: - ④ 城市维度与未填写并集

    func testCityFilterAndUnsetUnion() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let database = try fixture.readOnlyDatabase()

        var cityFilter = AssetFilter()
        cityFilter.cities = ["上海"]
        let shanghai = try fixture.catalog.loadAssets(filter: cityFilter, limit: 1_000)
        let expectedShanghai = try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND city = '上海';")
        XCTAssertEqual(shanghai.count, expectedShanghai)
        XCTAssertTrue(shanghai.allSatisfy { $0.city == "上海" })

        var unsetFilter = AssetFilter()
        unsetFilter.unsetDimensions = [.city]
        let unset = try fixture.catalog.loadAssets(filter: unsetFilter, limit: 1_000)
        let expectedUnset = try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND (city IS NULL OR city = '');")
        XCTAssertEqual(unset.count, expectedUnset)
        XCTAssertGreaterThanOrEqual(unset.count, 4, "city 未填写行数应 ≥ 4（用于测 __unset__ 筛选）")
        XCTAssertTrue(unset.allSatisfy { ($0.city ?? "").isEmpty })

        // 「某个城市 ∪ 未填写」= 两者行数相加，验证筛选 SQL 的 OR 展开。
        var unionFilter = AssetFilter()
        unionFilter.cities = ["上海"]
        unionFilter.unsetDimensions = [.city]
        let union = try fixture.catalog.loadAssets(filter: unionFilter, limit: 1_000)
        XCTAssertEqual(union.count, expectedShanghai + expectedUnset)
        XCTAssertEqual(try fixture.catalog.count(filter: unionFilter), union.count)
    }

    // MARK: - ⑤ 多主题任一命中

    func testMultiThemeMatchesAnyThemeInJsonArray() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let database = try fixture.readOnlyDatabase()

        var filter = AssetFilter()
        filter.themes = ["建筑"]
        let assets = try fixture.catalog.loadAssets(filter: filter, limit: 1_000)
        let expected = try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND (theme = '建筑' OR themes_json LIKE '%\"建筑\"%');")
        XCTAssertEqual(assets.count, expected)
        XCTAssertGreaterThan(assets.count, 0)
        XCTAssertTrue(assets.allSatisfy { $0.themes.contains("建筑") })

        // 「建筑」在样例库里只作为第 2/3 主题出现，命中必须走 themes_json 分支。
        XCTAssertTrue(assets.contains { $0.themes.first != "建筑" }, "没有命中 themes_json 多主题分支")
        XCTAssertTrue(assets.contains { $0.themes.count >= 3 }, "没有三主题素材")

        let multiThemeRows = try database.query("SELECT themes_json FROM assets;") { $0.string(0) ?? "" }
            .filter { (Catalog.decodeStringArray($0) ?? []).count >= 2 }
        XCTAssertGreaterThanOrEqual(multiThemeRows.count, 10)

        // theme 列始终等于 themes_json 的第一项。
        let rows = try database.query("SELECT theme, themes_json FROM assets;") { ($0.string(0), $0.string(1) ?? "") }
        for (theme, themeJSON) in rows {
            XCTAssertEqual(theme, Catalog.decodeStringArray(themeJSON)?.first, "theme 应等于 themes_json[0]（\(themeJSON)）")
        }
    }

    // MARK: - ⑥ is_pick 三态

    func testPickTriStateAndFacetCounts() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let database = try fixture.readOnlyDatabase()

        let picked = try scalar(database, "SELECT count(*) FROM assets WHERE is_pick = 1;")
        let rejected = try scalar(database, "SELECT count(*) FROM assets WHERE is_pick = 0;")
        let unmarked = try scalar(database, "SELECT count(*) FROM assets WHERE is_pick IS NULL;")
        XCTAssertGreaterThan(picked, 0, "必须有精选素材")
        XCTAssertGreaterThan(rejected, 0, "必须有废片素材")
        XCTAssertGreaterThan(unmarked, 0, "必须有未标记素材")
        XCTAssertEqual(picked + rejected + unmarked, 28)

        let facets = try fixture.catalog.loadFacets()
        XCTAssertEqual(facets.picked, try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND is_pick = 1;"))
        XCTAssertEqual(facets.rejected, try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND is_pick = 0;"))
        XCTAssertEqual(facets.unpicked, try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND is_pick IS NULL;"))

        var pickFilter = AssetFilter()
        pickFilter.pick = 1
        let pickedAssets = try fixture.catalog.loadAssets(filter: pickFilter, limit: 1_000)
        XCTAssertEqual(pickedAssets.count, facets.picked)
        XCTAssertTrue(pickedAssets.allSatisfy { $0.isPick == 1 })

        var rejectedFilter = AssetFilter()
        rejectedFilter.pick = 0
        XCTAssertEqual(try fixture.catalog.count(filter: rejectedFilter), facets.rejected)
    }

    // MARK: - ⑦ 评分与排序

    func testRatingFilterAndSortDescriptors() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let database = try fixture.readOnlyDatabase()

        var filter = AssetFilter()
        filter.ratings = [4, 5]
        let highRated = try fixture.catalog.loadAssets(filter: filter, limit: 1_000)
        XCTAssertEqual(highRated.count, try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND rating IN (4, 5);"))
        XCTAssertTrue(highRated.allSatisfy { $0.rating >= 4 })

        // 评分降序：首项必须是 5 星，且整体单调不增。
        let byRating = try fixture.catalog.loadAssets(sort: SortDescriptor(key: .rating, ascending: false), limit: 1_000)
        XCTAssertEqual(byRating.first?.rating, try scalar(database, "SELECT max(rating) FROM assets\(Self.coverOnlyWhere);"))
        XCTAssertEqual(byRating.map(\.rating), byRating.map(\.rating).sorted(by: >))

        // 拍摄时间降序：首项必须是 captured_ms 最大的那一行。
        let byCaptured = try fixture.catalog.loadAssets(sort: SortDescriptor(key: .captured, ascending: false), limit: 1_000)
        let maxCapturedMillis = try scalar(database, "SELECT CAST(max(captured_ms) AS INTEGER) FROM assets\(Self.coverOnlyWhere) AND captured_ms IS NOT NULL;")
        XCTAssertEqual(byCaptured.first?.capturedAt.map { Int($0.timeIntervalSince1970 * 1000) }, maxCapturedMillis)
        let timestamps = byCaptured.compactMap { $0.capturedAt?.timeIntervalSince1970 }
        XCTAssertEqual(timestamps, timestamps.sorted(by: >))

        // 升序排序与降序首尾对应。
        let ascending = try fixture.catalog.loadAssets(sort: SortDescriptor(key: .captured, ascending: true), limit: 1_000)
        XCTAssertEqual(ascending.first?.id, byCaptured.last?.id)
    }

    // MARK: - ⑧ facets

    func testFacetsCountsMatchVisibleRows() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let database = try fixture.readOnlyDatabase()

        let facets = try fixture.catalog.loadFacets()
        let visible = try fixture.catalog.count()
        XCTAssertEqual(facets.total, visible)
        XCTAssertEqual(facets.kinds.reduce(0) { $0 + $1.count }, facets.total, "kind 分组计数之和")
        XCTAssertEqual(facets.formats.reduce(0) { $0 + $1.count }, facets.total, "format 分组计数之和")
        XCTAssertEqual(facets.cities.reduce(0) { $0 + $1.count } + (facets.unsetCounts[.city] ?? -1), facets.total, "城市分组 + 未填写")
        XCTAssertEqual(facets.processed, try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND post_process = 2;"))
        XCTAssertEqual(facets.unprocessed, facets.total - facets.processed)
        XCTAssertEqual(facets.logCount, try scalar(database, "SELECT count(*) FROM assets\(Self.coverOnlyWhere) AND is_log <> 0;"))
        XCTAssertEqual(facets.logCount, 2, "样例库应有 2 行 Log 素材")
        XCTAssertGreaterThanOrEqual(facets.themes.count, 5)
        XCTAssertEqual(facets.ratings.map(\.value).compactMap(Int.init).sorted(),
                       facets.ratings.map(\.value).compactMap(Int.init).sorted(), "评分 facet 应按数值升序")
        XCTAssertNotNil(facets.ratings.first { $0.value == "0" }, "rating=0 也应出现在 facet 里")
    }

    // MARK: - ⑨ 批量更新写回（临时副本）

    func testBatchUpdateWritesBackOnTemporaryCopy() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        guard let original = try fixture.asset(search: "bund") else {
            return XCTFail("样例库里找不到 bund 素材")
        }
        XCTAssertEqual(original.rating, 5)
        XCTAssertEqual(original.city, "上海")

        let (temporary, temporaryDirectory) = try fixture.temporaryCatalog()
        guard var target = try temporary.loadAssets(limit: 5_000).first(where: { $0.id == original.id }) else {
            return XCTFail("临时副本里找不到目标素材")
        }

        var patch = AssetPatch()
        patch.rating = 1
        patch.isPick = .set(0)
        patch.city = .set("深圳")
        patch.country = .set("中国")
        patch.province = .set("广东省")
        patch.tone = .set("暖调")
        patch.colorLabel = .set("紫")
        patch.themes = .set(["测试主题", "单元测试"])
        try temporary.update(patch: patch, ids: [original.id])

        target = try temporary.loadAssets(limit: 5_000).first { $0.id == original.id }!
        XCTAssertEqual(target.rating, 1)
        XCTAssertEqual(target.isPick, 0)
        XCTAssertEqual(target.city, "深圳")
        XCTAssertEqual(target.country, "中国")
        XCTAssertEqual(target.province, "广东省")
        XCTAssertEqual(target.tone, "暖调")
        XCTAssertEqual(target.colorLabel, "紫")
        XCTAssertEqual(target.themes, ["测试主题", "单元测试"])
        let themeColumn = try Database(path: temporaryDirectory.appendingPathComponent("catalog.db").path, readOnly: true)
            .query("SELECT theme FROM assets WHERE id = ?;", binds: [original.id]) { $0.string(0) }
        XCTAssertEqual(themeColumn.first ?? nil, "测试主题", "themes 更新时 theme 列应写第一项")

        // clear 语义：可空字段清空，is_pick 回到「未标记」。
        // 注意 themes_json 在 Windows schema 里是 NOT NULL DEFAULT '[]'，
        // 所以清空主题要用 .set([])（写 "[]" 且 theme 置 NULL），不能用 .clear。
        var clearPatch = AssetPatch()
        clearPatch.city = .clear
        clearPatch.tone = .clear
        clearPatch.colorLabel = .clear
        clearPatch.isPick = .clear
        clearPatch.themes = .set([])
        try temporary.update(patch: clearPatch, ids: [original.id])
        let cleared = try temporary.loadAssets(limit: 5_000).first { $0.id == original.id }!
        XCTAssertNil(cleared.city)
        XCTAssertTrue(cleared.themes.isEmpty)
        XCTAssertNil(cleared.tone)
        XCTAssertNil(cleared.colorLabel)
        XCTAssertNil(cleared.isPick, "is_pick 清空后应回到未标记三态")
        let clearedRow = try Database(path: temporaryDirectory.appendingPathComponent("catalog.db").path, readOnly: true)
            .query("SELECT themes_json, theme FROM assets WHERE id = ?;", binds: [original.id]) {
                ($0.string(0) ?? "", $0.string(1))
            }
        XCTAssertEqual(clearedRow.first?.0, "[]", "themes 清空后 themes_json 应为 JSON 空数组")
        XCTAssertNil(clearedRow.first?.1, "themes 清空后 theme 列应为 NULL")

        // fixture 必须原封不动。
        let pristine = try fixture.asset(search: "bund")!
        XCTAssertEqual(pristine.rating, 5, "临时副本的写操作污染了 fixture")
        XCTAssertEqual(pristine.city, "上海")
        XCTAssertEqual(pristine.themes, ["城市夜景", "建筑", "蓝调"])
    }

    // MARK: - ⑩ 合集 CRUD（临时副本）

    func testCollectionCRUDOnTemporaryCopy() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let (temporary, _) = try fixture.temporaryCatalog()

        let seeded = try temporary.collections()
        XCTAssertEqual(seeded.count, 3)
        XCTAssertEqual(seeded.map(\.name), ["城市夜景精选", "胶片调色", "巴黎旅行"])
        XCTAssertTrue(seeded.allSatisfy { $0.count >= 3 }, "每个内置合集都应 ≥3 个素材")

        let assets = try temporary.loadAssets(limit: 2)
        XCTAssertEqual(assets.count, 2)

        let newID = try temporary.createCollection(name: "单元测试合集")
        var collections = try temporary.collections()
        XCTAssertEqual(collections.count, 4)
        XCTAssertEqual(collections.first { $0.id == newID }?.count, 0)

        try temporary.addToCollection(collectionID: newID, assetIDs: assets.map(\.id))
        collections = try temporary.collections()
        XCTAssertEqual(collections.first { $0.id == newID }?.count, 2, "addToCollection 后的计数")

        try temporary.renameCollection(id: newID, name: "单元测试合集（改名）")
        XCTAssertEqual(try temporary.collections().first { $0.id == newID }?.name, "单元测试合集（改名）")

        try temporary.removeFromCollection(collectionID: newID, assetIDs: [assets[0].id])
        XCTAssertEqual(try temporary.collections().first { $0.id == newID }?.count, 1)

        try temporary.deleteCollection(id: newID)
        collections = try temporary.collections()
        XCTAssertEqual(collections.count, 3)
        XCTAssertFalse(collections.contains { $0.id == newID })

        // fixture 仍只有 3 个合集。
        XCTAssertEqual(try fixture.catalog.collections().count, 3)
    }

    // MARK: - ⑪ 合并组与展开

    func testMergeGroupsAndExpandedIDs() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }

        let groups = try fixture.catalog.mergeGroups()
        XCTAssertEqual(groups.count, 2)
        let visibleCount = try fixture.catalog.count()
        let rawCount = try scalar(try fixture.readOnlyDatabase(), "SELECT count(*) FROM assets;")
        XCTAssertEqual(rawCount, 28)
        XCTAssertLessThan(visibleCount, rawCount, "折叠视图应隐藏非封面成员")

        var memberIDs: [String] = []
        for group in groups {
            let members = try fixture.catalog.groupMembers(groupID: group.id)
            XCTAssertGreaterThanOrEqual(members.count, 2)
            XCTAssertEqual(members.count, group.count, "「\(group.name)」mergeGroups().count 与成员数一致")
            XCTAssertNotNil(group.coverAssetID)
            XCTAssertTrue(members.contains { $0.id == group.coverAssetID })
            XCTAssertEqual(members.filter(\.isGroupCover).count, 1)
            XCTAssertEqual(members.map(\.groupID), members.map { _ in group.id }, "每个成员都应带 group_id")
            memberIDs.append(contentsOf: members.map(\.id))

            let expanded = try fixture.catalog.expandedIDs(for: [group.coverAssetID!])
            XCTAssertEqual(Set(expanded), Set(members.map(\.id)), "「\(group.name)」expandedIDs 应展开整组")
            XCTAssertEqual(try fixture.catalog.loadAssets(limit: 100, expandedGroupID: group.id).count, members.count,
                           "按组展开后的行数")
        }
        XCTAssertEqual(Set(memberIDs).count, 5, "两组共 5 个成员")
        XCTAssertEqual(visibleCount + (memberIDs.count - groups.count), rawCount,
                       "默认可见 = 原始 - (成员 - 组数)")

        // position 递增：groupMembers 的顺序即 position 顺序。
        let nightGroup = groups.first { $0.name.contains("外滩") }
        XCTAssertNotNil(nightGroup)
        let ordered = try fixture.catalog.groupMembers(groupID: nightGroup!.id).map(\.fileName)
        XCTAssertEqual(ordered, ["SH_20260819_0001_bund.jpg", "SH_20260819_0003_lujiazui.jpg"])

        // 独立素材（不在任何组）展开后只有自己。
        let standalone = try fixture.asset(search: "breakfast")!
        XCTAssertEqual(try fixture.catalog.expandedIDs(for: [standalone.id]), [standalone.id])
    }

    // MARK: - ⑫ XMP

    func testXMPPacketContainsRatingSubjectAndCity() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let asset = try fixture.asset(search: "bund")!
        let packet = XMPWriter.packet(for: asset)

        XCTAssertTrue(packet.contains("xmp:Rating=\"5\""))
        XCTAssertTrue(packet.contains("photoshop:City=\"上海\""))
        XCTAssertTrue(packet.contains("photoshop:Country=\"中国\""))
        XCTAssertTrue(packet.contains("<dc:subject>"))
        XCTAssertTrue(packet.contains("<rdf:Bag>"))
        for keyword in asset.keywords {
            XCTAssertTrue(packet.contains("<rdf:li>\(keyword)</rdf:li>"), "缺少关键词：\(keyword)")
        }
        XCTAssertTrue(packet.contains("xmp:Label=\"蓝\""))
        XCTAssertTrue(packet.contains("<dc:title>"))
        XCTAssertTrue(packet.contains(asset.title ?? ""))
        XCTAssertEqual(XMPWriter.xmpURL(for: asset).lastPathComponent, "SH_20260819_0001_bund.xmp")
    }

    // MARK: - ⑬ 缩略图命名与缓存命中

    func testThumbnailNamingAndCacheHits() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }

        // ThumbnailNaming 是 internal，测试用 @testable 访问。
        // 命名约定：`{40 hex 键}[_{修订号}]_{尺寸}[_lut].jpg`，尺寸取最后一段数字。
        let hash = "0123456789abcdef0123456789abcdef01234567"
        let parsed = ThumbnailNaming.parse("\(hash)_1_360.jpg")
        XCTAssertEqual(parsed?.key, hash)
        XCTAssertEqual(parsed?.revision, "1")
        XCTAssertEqual(parsed?.size, 360)
        // Windows 风格的修订段（d2/c3…）不是数字，但依然能解析，尺寸取最后一段数字。
        let windowsStyle = ThumbnailNaming.parse("\(hash)_d2_360.jpg")
        XCTAssertEqual(windowsStyle?.key, hash)
        XCTAssertEqual(windowsStyle?.revision, "d2")
        XCTAssertEqual(windowsStyle?.size, 360)
        XCTAssertEqual(ThumbnailNaming.parse("\(hash)_d2_360_lut.jpg")?.size, 360)
        XCTAssertNil(ThumbnailNaming.parse("not-a-hash_360.jpg"))
        XCTAssertEqual(ThumbnailNaming.parse("\(hash)_360.jpg")?.size, 360)
        XCTAssertEqual(ThumbnailNaming.parse("\(hash)_360.jpg")?.revision, "")

        // DB 里的 thumb_key 是 `{sha1}_1`，索引前会归一成纯 40 位键。
        XCTAssertEqual(ThumbnailStore.normalizedKey("\(hash.uppercased())_1"), hash)
        XCTAssertEqual(ThumbnailStore.normalizedKey("\(hash)_lut"), hash)
        XCTAssertEqual(ThumbnailStore.normalizedKey(hash), hash)

        let store = ThumbnailStore()
        store.configure(cacheDirectory: fixture.thumbsURL)
        XCTAssertEqual(store.cachedThumbnailCount(), 20, "Cache/thumbs 下应索引 20 个缩略图")

        let assets = try fixture.allAssets()
        XCTAssertEqual(assets.count, 28)
        XCTAssertEqual(store.cachedHitCount(for: assets), 20, "20 张命中缓存，8 张走 QuickLook 兜底")

        let database = try fixture.readOnlyDatabase()
        let rows = try database.query("SELECT full_path, thumb_key FROM assets;") { ($0.string(0) ?? "", $0.string(1)) }
        let withThumbKey = rows.filter { ($0.1 ?? "").isEmpty == false }
        XCTAssertEqual(withThumbKey.count, 20)
        for (path, thumbKey) in withThumbKey {
            guard let thumbKey else { continue }
            let expectedKey = SHA1.hexString(Data(path.lowercased().utf8))
            XCTAssertEqual(thumbKey.split(separator: "_").first.map(String.init), expectedKey,
                           "thumb_key 应等于 sha1(full_path 小写)")
            XCTAssertTrue(FileManager.default.fileExists(atPath: fixture.thumbsURL.appendingPathComponent("\(thumbKey)_360.jpg").path))
        }

        // 有 thumb_key 的素材能解析出缓存文件，没有的落到 QuickLook。
        let cachedAssets = assets.filter { ($0.thumbKey ?? "").isEmpty == false }
        XCTAssertEqual(cachedAssets.count, 20)
        for asset in cachedAssets {
            let expectedKey = asset.thumbKey.flatMap { $0.split(separator: "_").first.map(String.init) }
            XCTAssertEqual(ThumbnailStore.normalizedKey(asset.thumbKeyCandidate), expectedKey,
                           "thumbKeyCandidate 归一化后应等于 40 位缩略图键")
        }
        // 没有 thumb_key 的 8 行：缩略图键回退为 sha1(路径)，但缓存里没有文件 → QuickLook 兜底。
        let missingKey = assets.filter { ($0.thumbKey ?? "").isEmpty }
        XCTAssertEqual(missingKey.count, 8)
        XCTAssertEqual(store.cachedHitCount(for: missingKey), 0)
    }

    // MARK: - ⑭ libraries / 元数据

    func testLibrariesAndKeywordMetadata() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let database = try fixture.readOnlyDatabase()

        let libraries = try fixture.catalog.libraries()
        XCTAssertEqual(libraries.count, 1)
        XCTAssertEqual(URL(fileURLWithPath: libraries[0].rootPath).resolvingSymlinksInPath().standardizedFileURL.path,
                       fixture.directory.path, "libraries.root_path 应指向样例库目录")
        // Catalog.libraries() 的 count 恒为 0（SQL 只取 id/name/root_path），asset_count 直接查库。
        XCTAssertEqual(try scalar(database, "SELECT asset_count FROM libraries;"), 28, "libraries.asset_count")

        let rows = try database.query("SELECT keywords_flat, keywords_json, title, city FROM assets;") {
            ($0.string(0) ?? "", $0.string(1) ?? "", $0.string(2), $0.string(3))
        }
        for (flat, json, _, _) in rows {
            guard let keywords = Catalog.decodeStringArray(json) else {
                XCTFail("keywords_json 解码失败：\(json)")
                continue
            }
            XCTAssertFalse(keywords.isEmpty)
            XCTAssertEqual(flat, keywords.joined(separator: "|"), "keywords_flat 必须是 | 分隔")
        }

        let emptyCityCount = try scalar(database, "SELECT count(*) FROM assets WHERE city IS NULL OR city = '';")
        XCTAssertGreaterThanOrEqual(emptyCityCount, 4, "样例库必须保留 ≥4 行未填写城市")
        let unsetCityAssets = try fixture.catalog.loadAssets(filter: {
            var filter = AssetFilter()
            filter.unsetDimensions = [.city]
            return filter
        }(), limit: 1_000)
        XCTAssertTrue(unsetCityAssets.contains { $0.fileName == "SH_20260819_0003_lujiazui.jpg" || ($0.city ?? "").isEmpty })
    }

    // MARK: - ⑮ 分页与覆盖行过滤

    func testPaginationAndCoverOnlyDefault() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }

        let firstPage = try fixture.catalog.loadAssets(sort: SortDescriptor(key: .fileName, ascending: true), limit: 5, offset: 0)
        let secondPage = try fixture.catalog.loadAssets(sort: SortDescriptor(key: .fileName, ascending: true), limit: 5, offset: 5)
        XCTAssertEqual(firstPage.count, 5)
        XCTAssertEqual(secondPage.count, 5)
        XCTAssertTrue(Set(firstPage.map(\.id)).isDisjoint(with: Set(secondPage.map(\.id))), "分页不应重复")

        // 默认覆盖行过滤：不返回被折叠的非封面成员，但展开组后可见。
        let groups = try fixture.catalog.mergeGroups()
        guard let coverID = groups.first?.coverAssetID else {
            return XCTFail("合并组缺少封面")
        }
        let hiddenMembers = try fixture.catalog.groupMembers(groupID: groups[0].id)
            .filter { $0.id != coverID }
            .map(\.id)
        XCTAssertFalse(hiddenMembers.isEmpty)
        let visibleIDs = Set(try fixture.catalog.loadAssets(limit: 5_000).map(\.id))
        for hiddenID in hiddenMembers {
            XCTAssertFalse(visibleIDs.contains(hiddenID), "非封面成员不应出现在默认视图")
        }
        let expandedIDs = Set(try fixture.catalog.loadAssets(limit: 5_000, expandedGroupID: groups[0].id).map(\.id))
        for hiddenID in hiddenMembers {
            XCTAssertTrue(expandedIDs.contains(hiddenID), "展开组后应包含非封面成员")
        }
    }
    // MARK: - ⑯ RAW+JPG 折叠与画廊取图

    /// 折叠 RAW+JPG 配对：同目录同名的 JPG 伴生行应隐藏，RAW 作为封面保留。
    /// 样例库本身没有 RAW 行，这里在临时副本里补两条同基名记录。
    func testFoldRawPairsHidesJPEGWhenRawSiblingExists() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        let (temporary, directory) = try fixture.temporaryCatalog()
        let database = try Database(path: directory.appendingPathComponent("catalog.db").path, readOnly: false)

        func insertCopy(id: String, fileName: String, format: String) throws {
            let fullPath = "\(directory.path)/media/pairs/\(fileName)"
            // 以样例库任意一行为模板，覆盖主键与路径相关列；其余列走 schema 默认值。
            let insert = try database.prepare("""
            INSERT INTO assets (id, full_path, file_name, format_code, group_id, is_group_cover)
            SELECT ?, ?, ?, ?, NULL, 1 FROM assets LIMIT 1;
            """)
            try insert.bindAll([id, fullPath, fileName, format])
            _ = try insert.step()
        }
        try insertCopy(id: "pair-jpg", fileName: "PAIR_0001.jpg", format: "jpg")
        try insertCopy(id: "pair-raw", fileName: "PAIR_0001.CR3", format: "CR3")

        let plainIDs = Set(try temporary.loadAssets(limit: 5_000).map(\.id))
        XCTAssertTrue(plainIDs.contains("pair-jpg"), "未折叠时 JPG 伴生文件应可见")
        XCTAssertTrue(plainIDs.contains("pair-raw"), "未折叠时 RAW 应可见")

        var folded = AssetFilter()
        folded.foldRawPairs = true
        let foldedAssets = try temporary.loadAssets(filter: folded, limit: 5_000)
        let foldedIDs = Set(foldedAssets.map(\.id))
        XCTAssertTrue(foldedIDs.contains("pair-raw"), "RAW 应作为折叠后的封面保留")
        XCTAssertFalse(foldedIDs.contains("pair-jpg"), "有同基名 RAW 时 JPG 应被折叠")
        XCTAssertEqual(try temporary.count(filter: folded), foldedAssets.count,
                       "折叠后的 count 必须与列表条数一致")
        XCTAssertEqual(plainIDs.count - foldedIDs.count, 1, "折叠只应隐藏一个 JPG 伴生行")

        // 不同基名、不同目录都不应被误折叠。
        XCTAssertTrue(foldedIDs.contains { $0.hasPrefix("pair-raw") })
        XCTAssertTrue(plainIDs.subtracting(["pair-jpg"]).isSubset(of: foldedIDs),
                      "除 JPG 伴生行外，样例库原有可见素材不应被折叠")
    }

    /// 画廊用 ImageLoader 直接读原图（ImageIO 降采样），视频等无解码器文件返回 nil。
    func testImageLoaderLoadsJPEGPreview() throws {
        let fixture = try makeFixture()
        defer { fixture.cleanup() }
        guard let imageAsset = try fixture.allAssets().first(where: {
            $0.formatCode.lowercased() == "jpg" && FileManager.default.fileExists(atPath: $0.fileURL.path)
        }) else {
            throw XCTSkip("样例库里没有可读取的 JPEG 素材")
        }
        let image = ImageLoader.previewImage(at: imageAsset.fileURL, maxPixelSize: 1_600)
        XCTAssertNotNil(image, "JPEG 应该能通过 ImageIO 解出预览图")
        XCTAssertEqual(image?.size.width, 1_200, "样例 JPEG 都是 1200x800，降采样后宽度不变")
        XCTAssertNil(ImageLoader.previewImage(at: URL(fileURLWithPath: "/tmp/definitely-missing-stj.jpg")),
                     "不存在的文件应返回 nil 而不是崩溃")
    }
    // MARK: 配对导出

    func testPairExporterFindsRawSibling() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("stj-pair-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let jpeg = directory.appendingPathComponent("PAIR_0002.jpg")
        let raw = directory.appendingPathComponent("PAIR_0002.CR3")
        let lone = directory.appendingPathComponent("LONE_0001.jpg")
        try Data("jpeg".utf8).write(to: jpeg)
        try Data("raw".utf8).write(to: raw)
        try Data("lone".utf8).write(to: lone)

        let jpegAsset = Asset(path: jpeg.path, fileName: jpeg.lastPathComponent, formatCode: "jpg")
        let rawAsset = Asset(path: raw.path, fileName: raw.lastPathComponent, formatCode: "cr3")
        let loneAsset = Asset(path: lone.path, fileName: lone.lastPathComponent, formatCode: "jpg")

        let planFromJPEG = PairExporter.plan(for: jpegAsset)
        XCTAssertEqual(planFromJPEG.jpeg?.resolvingSymlinksInPath(), jpeg.resolvingSymlinksInPath())
        XCTAssertEqual(planFromJPEG.raw?.resolvingSymlinksInPath(), raw.resolvingSymlinksInPath())
        XCTAssertEqual(planFromJPEG.urls.count, 2)

        let planFromRAW = PairExporter.plan(for: rawAsset)
        XCTAssertEqual(planFromRAW.jpeg?.resolvingSymlinksInPath(), jpeg.resolvingSymlinksInPath())
        XCTAssertEqual(planFromRAW.raw?.resolvingSymlinksInPath(), raw.resolvingSymlinksInPath())

        let lonePlan = PairExporter.plan(for: loneAsset)
        XCTAssertEqual(lonePlan.jpeg?.resolvingSymlinksInPath(), lone.resolvingSymlinksInPath())
        XCTAssertNil(lonePlan.raw)
        XCTAssertEqual(lonePlan.urls.count, 1)

        let destination = FileManager.default.temporaryDirectory
            .appendingPathComponent("stj-pair-out-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: destination) }
        let written = try PairExporter.copy([planFromJPEG, lonePlan], to: destination)
        XCTAssertEqual(written.count, 3)
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent("PAIR_0002.jpg").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent("PAIR_0002.CR3").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent("LONE_0001.jpg").path))

        // 重复导出不会覆盖，自动加后缀
        let again = try PairExporter.copy([planFromJPEG], to: destination)
        XCTAssertEqual(again.count, 2)
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent("PAIR_0002-1.jpg").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent("PAIR_0002-1.CR3").path))
    }


    // MARK: v1.25.2 扫描导入（P2）

    /// 把 fixture 里的真实媒体复制到临时目录，模拟"新素材目录"。
    private func makeImportFolder(images: Int, videos: Int) throws -> (root: URL, media: [URL]) {
        let fixture = try makeFixture()
        let root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stjimport-\(UUID().uuidString)", isDirectory: true)
        let mediaDirectory = root.appendingPathComponent("media", isDirectory: true)
        try FileManager.default.createDirectory(at: mediaDirectory, withIntermediateDirectories: true)
        var copied: [URL] = []
        let imageSource = fixture.directory.appendingPathComponent("media/images", isDirectory: true)
        let videoSource = fixture.directory.appendingPathComponent("media/videos", isDirectory: true)
        let imageFiles = try FileManager.default.contentsOfDirectory(at: imageSource, includingPropertiesForKeys: nil)
            .filter { ["jpg", "jpeg"].contains($0.pathExtension.lowercased()) }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        for url in imageFiles.prefix(images) {
            let target = mediaDirectory.appendingPathComponent("IMPORT_" + url.lastPathComponent)
            try FileManager.default.copyItem(at: url, to: target)
            copied.append(target)
        }
        if videos > 0 {
            let videoFiles = try FileManager.default.contentsOfDirectory(at: videoSource, includingPropertiesForKeys: nil)
                .filter { ["mov", "mp4"].contains($0.pathExtension.lowercased()) }
                .sorted { $0.lastPathComponent < $1.lastPathComponent }
            for url in videoFiles.prefix(videos) {
                let target = mediaDirectory.appendingPathComponent("IMPORT_" + url.lastPathComponent)
                try FileManager.default.copyItem(at: url, to: target)
                copied.append(target)
            }
        }
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return (root, copied)
    }

    func testImporterCreatesDatabaseAndThumbnails() throws {
        let (root, _) = try makeImportFolder(images: 2, videos: 1)
        let first = try Importer.importFolder(root)

        XCTAssertEqual(first.added, 3, "首次导入应新增 3 条")
        XCTAssertEqual(first.skipped, 0)
        XCTAssertEqual(first.failed, 0)
        XCTAssertEqual(first.totalAssets, 3)
        XCTAssertGreaterThanOrEqual(first.thumbnails, 2, "两张图片至少各生成一次缩略图")

        let databaseURL = root.appendingPathComponent("catalog.db")
        XCTAssertTrue(FileManager.default.fileExists(atPath: databaseURL.path))
        let catalog = try Catalog(directory: root)
        XCTAssertEqual(try catalog.count(), 3)

        let assets = try catalog.loadAssets(limit: 10)
        XCTAssertEqual(assets.filter { $0.kind == 1 }.count, 2)
        XCTAssertEqual(assets.filter { $0.kind == 2 }.count, 1)
        for asset in assets {
            XCTAssertTrue(asset.sizeBytes > 0, "\(asset.fileName) 应记录文件大小")
            XCTAssertNotNil(asset.width)
            XCTAssertNotNil(asset.height)
            XCTAssertNotEqual(asset.formatCode, "")
            XCTAssertNotNil(asset.capturedAt, "\(asset.fileName) 应推断拍摄/修改时间")
        }
        let video = assets.first { $0.kind == 2 }
        XCTAssertNotNil(video?.durationSeconds)
        XCTAssertTrue(video?.isVideo == true)

        // 缩略图文件必须符合 ThumbnailStore 的命名约定，并且可被解析/命中
        let thumbs = root.appendingPathComponent("Cache/thumbs", isDirectory: true)
        let thumbFiles = try FileManager.default.contentsOfDirectory(at: thumbs, includingPropertiesForKeys: nil)
        XCTAssertGreaterThanOrEqual(thumbFiles.count, 4, "两张图各 360/640 两档")
        for asset in assets where asset.kind == 1 {
            guard let key = asset.thumbKey, !key.isEmpty else {
                XCTFail("\(asset.fileName) 缺少 thumb_key"); continue
            }
            XCTAssertNotNil(ThumbnailNaming.parse("\(key.lowercased())_360.jpg"), "缩略图文件名应可被解析：\(key)")
            let url = thumbs.appendingPathComponent("\(key.lowercased())_360.jpg")
            XCTAssertTrue(FileManager.default.fileExists(atPath: url.path), "缺少 \(url.lastPathComponent)")
        }

        // 库计数
        let database = try Database(path: databaseURL.path, readOnly: true)
        XCTAssertEqual(try database.scalarInt("SELECT count(*) FROM libraries;"), 1)
        XCTAssertEqual(try database.scalarInt("SELECT asset_count FROM libraries LIMIT 1;"), 3)
        XCTAssertEqual(try database.scalarInt("SELECT count(*) FROM meta WHERE key='schema_version';"), 1)

        // 第二次导入：全部跳过
        let second = try Importer.importFolder(root)
        XCTAssertEqual(second.added, 0)
        XCTAssertEqual(second.updated, 0)
        XCTAssertEqual(second.skipped, 3)

        // 增删文件后：新增 1 条
        let extra = try FileManager.default.contentsOfDirectory(
            at: root.appendingPathComponent("media"),
            includingPropertiesForKeys: nil
        ).first { $0.pathExtension == "jpg" }!
        let another = root.appendingPathComponent("media/IMPORT_EXTRA.jpg")
        try FileManager.default.copyItem(at: extra, to: another)
        let third = try Importer.importFolder(root)
        XCTAssertEqual(third.added, 1)
        XCTAssertEqual(third.totalAssets, 4)
    }


    func testImporterDeduplicatesAcrossSymlinkedRoot() throws {
        let root = try makeImportFolder(images: 2, videos: 0).root
        let alias = FileManager.default.temporaryDirectory
            .appendingPathComponent("stj-alias-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: root)
        addTeardownBlock { try? FileManager.default.removeItem(at: alias) }

        let first = try Importer.importFolder(root)
        XCTAssertEqual(first.added, 2)
        XCTAssertEqual(first.skipped, 0)

        // 通过符号链接路径再次导入：路径解析到同一文件，必须全部跳过，不能重复入库。
        let second = try Importer.importFolder(alias)
        XCTAssertEqual(second.added, 0, "符号链接路径导致重复导入")
        XCTAssertEqual(second.skipped, 2)
        XCTAssertEqual(second.totalAssets, 2)
    }

    func testImporterKeepsUserEditsWhenFileChanges() throws {
        let (root, media) = try makeImportFolder(images: 1, videos: 0)
        try Importer.importFolder(root, generateThumbnails: false)
        let catalog = try Catalog(directory: root)
        let asset = try XCTUnwrap(try catalog.loadAssets(limit: 1).first)

        var patch = AssetPatch()
        patch.rating = 5
        patch.city = .set("上海")
        try catalog.update(patch: patch, ids: [asset.id])

        // 修改文件修改时间并重新导入 → 只更新技术元数据，用户编辑保留
        let newDate = Date().addingTimeInterval(60)
        try FileManager.default.setAttributes([.modificationDate: newDate], ofItemAtPath: media[0].path)
        let summary = try Importer.importFolder(root, generateThumbnails: false)
        XCTAssertEqual(summary.updated, 1)
        XCTAssertEqual(summary.added, 0)

        if let refreshed = try catalog.loadAssets(limit: 1).first {
            XCTAssertEqual(refreshed.rating, 5, "重扫不能清掉用户评分")
            XCTAssertEqual(refreshed.city, "上海", "重扫不能清掉用户填写的城市")
        } else {
            XCTFail("素材不应消失")
        }
    }

    // MARK: v1.25.2 框选几何

    func testMarqueeRectNormalizes() {
        let rect = MarqueeGeometry.rect(from: CGPoint(x: 120, y: 80), to: CGPoint(x: 20, y: 200))
        XCTAssertEqual(rect.origin.x, 20, accuracy: 0.001)
        XCTAssertEqual(rect.origin.y, 80, accuracy: 0.001)
        XCTAssertEqual(rect.width, 100, accuracy: 0.001)
        XCTAssertEqual(rect.height, 120, accuracy: 0.001)
    }

    func testMarqueeClickTolerance() {
        XCTAssertTrue(MarqueeGeometry.isClick(.zero))
        XCTAssertTrue(MarqueeGeometry.isClick(CGRect(x: 1, y: 1, width: 2, height: 2)))
        XCTAssertFalse(MarqueeGeometry.isClick(CGRect(x: 0, y: 0, width: 10, height: 2)))
        XCTAssertFalse(MarqueeGeometry.isClick(CGRect(x: 0, y: 0, width: 2, height: 10)))
    }

    func testMarqueeSelectionReplacesAddsAndToggles() {
        let frames: [String: CGRect] = [
            "a": CGRect(x: 0, y: 0, width: 100, height: 100),
            "b": CGRect(x: 120, y: 0, width: 100, height: 100),
            "c": CGRect(x: 0, y: 120, width: 100, height: 100),
            "d": CGRect(x: 400, y: 400, width: 100, height: 100)
        ]
        let rect = CGRect(x: 10, y: 10, width: 80, height: 80) // 只与 a 相交，不含 b/c/d
        XCTAssertEqual(MarqueeGeometry.selection(in: rect, frames: frames, current: [], shift: false, command: false), ["a"])
        XCTAssertEqual(MarqueeGeometry.selection(in: rect, frames: frames, current: ["c"], shift: true, command: false), ["a", "c"])
        XCTAssertEqual(MarqueeGeometry.selection(in: rect, frames: frames, current: ["a", "c"], shift: false, command: true), ["c"])
        XCTAssertEqual(MarqueeGeometry.selection(in: CGRect(x: -80, y: -80, width: 10, height: 10), frames: frames, current: ["d"], shift: false, command: false).isEmpty, true)
    }

    // MARK: v1.27.0 重复项检测与整理动作

    private func makeDate(_ year: Int, _ month: Int, _ day: Int) -> Date {
        var components = DateComponents()
        components.year = year
        components.month = month
        components.day = day
        components.hour = 12
        return Calendar(identifier: .gregorian).date(from: components) ?? Date()
    }

    func testDuplicateFinderGroupsBySizeAndDimensions() {
        let captured = makeDate(2024, 7, 4)
        var lower = Asset(path: "/tmp/dup/IMG_0001.jpg", fileName: "IMG_0001.jpg",
                          formatCode: "jpg", sizeBytes: 1000, width: 800, height: 600,
                          capturedAt: captured, rating: 2)
        lower.id = "lower"
        var higher = Asset(path: "/tmp/dup/IMG_0001 (2).jpg", fileName: "IMG_0001 (2).jpg",
                           formatCode: "jpg", sizeBytes: 1000, width: 800, height: 600,
                           capturedAt: captured, rating: 4)
        higher.id = "higher"
        var different = Asset(path: "/tmp/dup/OTHER.jpg", fileName: "OTHER.jpg",
                              formatCode: "jpg", sizeBytes: 1000, width: 400, height: 300,
                              capturedAt: captured)
        different.id = "different"

        let groups = DuplicateFinder.find(in: [lower, higher, different])
        XCTAssertEqual(groups.count, 1)
        XCTAssertEqual(groups[0].count, 2)
        XCTAssertEqual(groups[0].keepFirst?.id, "higher", "高分的那张应作为建议保留")
        XCTAssertEqual(groups[0].wastedBytes, 1000)
        XCTAssertFalse(groups[0].reason.isEmpty)

        // 精选优先于评分
        higher.isPick = nil
        lower.isPick = 1
        let picked = DuplicateFinder.find(in: [lower, higher, different])
        XCTAssertEqual(picked.first?.keepFirst?.id, "lower")
    }

    func testDuplicateFinderVerifiesContentHash() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("stj-dup-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }

        let sameA = directory.appendingPathComponent("A.jpg")
        let sameC = directory.appendingPathComponent("C.jpg")
        let other = directory.appendingPathComponent("B.jpg")
        let payload = Data(repeating: 0x11, count: 4096)
        try payload.write(to: sameA)
        try payload.write(to: sameC)
        try Data(repeating: 0x22, count: 4096).write(to: other)

        let assets = [sameA, other, sameC].map { url in
            Asset(path: url.path, fileName: url.lastPathComponent, formatCode: "jpg",
                  sizeBytes: 4096, width: 800, height: 600)
        }

        // 未校验内容时三张都进同一桶
        XCTAssertEqual(DuplicateFinder.find(in: assets).first?.count, 3)
        // 校验内容后只有真正相同的两张成组
        let verified = DuplicateFinder.find(in: assets, verifyContent: true)
        XCTAssertEqual(verified.count, 1)
        XCTAssertEqual(verified[0].count, 2)
        XCTAssertTrue(verified[0].reason.contains("哈希"))
        XCTAssertNotNil(ContentHasher.quickHash(at: sameA))
    }

    func testOrganizeRulesRenderTemplate() {
        var asset = Asset(path: "/tmp/organize/IMG_0001.jpg", fileName: "IMG_0001.jpg",
                          formatCode: "jpg", capturedAt: makeDate(2024, 7, 4))
        asset.city = "上海"
        asset.rating = 4

        let rules = OrganizeRules(template: "{year}/{month}-{city}/{name}")
        XCTAssertEqual(rules.relativePath(for: asset), "2024/07-上海/IMG_0001.jpg")

        let plain = OrganizeRules(template: "{original}")
        XCTAssertEqual(plain.relativePath(for: asset), "IMG_0001.jpg")

        // 非法字符与未知占位符都要被清理
        asset.city = "上/海:测试"
        let sanitized = OrganizeRules(template: "{city}/{unknown}-{original}")
        let path = sanitized.relativePath(for: asset)
        XCTAssertFalse(path.contains(":"))
        XCTAssertTrue(path.hasSuffix("IMG_0001.jpg"))
    }

    func testOrganizerCopiesAndRenamesOnConflict() throws {
        let source = FileManager.default.temporaryDirectory
            .appendingPathComponent("stj-org-src-\(UUID().uuidString)", isDirectory: true)
        let target = FileManager.default.temporaryDirectory
            .appendingPathComponent("stj-org-dst-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        addTeardownBlock {
            try? FileManager.default.removeItem(at: source)
            try? FileManager.default.removeItem(at: target)
        }
        let file = source.appendingPathComponent("A.jpg")
        try Data(repeating: 0x33, count: 1024).write(to: file)

        let asset = Asset(path: file.path, fileName: "A.jpg", formatCode: "jpg",
                          sizeBytes: 1024, capturedAt: makeDate(2024, 3, 14))
        let rules = OrganizeRules(template: "{original}")

        let first = Organizer.perform(assets: [asset], to: target, mode: .copy, rules: rules)
        XCTAssertEqual(first.copied, 1)
        XCTAssertEqual(first.failures.count, 0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: target.appendingPathComponent("A.jpg").path))

        // 再复制一次不能覆盖，自动变成 "A (2).jpg"
        let second = Organizer.perform(assets: [asset], to: target, mode: .copy, rules: rules)
        XCTAssertEqual(second.copied, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: target.appendingPathComponent("A (2).jpg").path))

        // skip 策略应跳过
        let skipRules = OrganizeRules(template: "{original}", conflictPolicy: .skip)
        let third = Organizer.perform(assets: [asset], to: target, mode: .copy, rules: skipRules)
        XCTAssertEqual(third.copied, 0)
        XCTAssertEqual(third.skipped, 1)
    }

    func testOrganizerMovesFileAndUpdatesCatalogPath() throws {
        let (root, _) = try makeImportFolder(images: 1, videos: 0)
        _ = try Importer.importFolder(root)
        let catalog = try Catalog(directory: root)
        let asset = try XCTUnwrap(try catalog.loadAssets(limit: 10).first)

        let destination = root.appendingPathComponent("整理输出", isDirectory: true)
        let rules = OrganizeRules(template: "{year}/{original}")
        let summary = Organizer.perform(assets: [asset], to: destination, mode: .move, rules: rules)

        XCTAssertEqual(summary.moved, 1)
        XCTAssertEqual(summary.failures.count, 0)
        XCTAssertFalse(FileManager.default.fileExists(atPath: asset.path), "移动后源文件不应还在")
        let moved = try XCTUnwrap(summary.relocated[asset.id])
        XCTAssertTrue(FileManager.default.fileExists(atPath: moved.path))

        try catalog.updateLocations(summary.relocated, relativeTo: root)
        let updated = try XCTUnwrap(try catalog.loadAssets(limit: 10).first)
        XCTAssertEqual(URL(fileURLWithPath: updated.path).resolvingSymlinksInPath().path,
                       moved.resolvingSymlinksInPath().path)
        XCTAssertEqual(updated.fileName, moved.lastPathComponent)
        XCTAssertNotNil(updated.relativePath, "移动后应回写相对路径")
    }
}

// MARK: - XMP 侧车读取与导入回填

final class XMPImportTests: XCTestCase {
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
        throw XCTSkip("缺少 .testdata 样例库，跳过 XMP 导入测试。")
    }

    private func makeImportDirectory() throws -> URL {
        let fixture = try fixtureDirectory()
        let root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stjxmp-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        let image = try FileManager.default
            .contentsOfDirectory(at: fixture.appendingPathComponent("media/images"),
                                 includingPropertiesForKeys: nil)
            .filter { ["jpg", "jpeg"].contains($0.pathExtension.lowercased()) }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }[0]
        try FileManager.default.copyItem(at: image, to: root.appendingPathComponent("XMP_0001.jpg"))
        return root
    }

    private static let sampleXMP = """
    <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
    <x:xmpmeta xmlns:x="adobe:ns:meta/">
     <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
      <rdf:Description rdf:about=""
          xmlns:xmp="http://ns.adobe.com/xap/1.0/"
          xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
          xmlns:dc="http://purl.org/dc/elements/1.1/"
          xmp:Rating="4" xmp:Label="蓝"
          photoshop:City="上海" photoshop:Country="中国" photoshop:State="上海市">
       <dc:subject><rdf:Bag><rdf:li>城市夜景</rdf:li><rdf:li>陆家嘴</rdf:li></rdf:Bag></dc:subject>
       <dc:title><rdf:Alt><rdf:li xml:lang="x-default">外滩夜色</rdf:li></rdf:Alt></dc:title>
       <dc:description><rdf:Alt><rdf:li xml:lang="x-default">黄浦江对岸</rdf:li></rdf:Alt></dc:description>
       <dc:creator><rdf:Seq><rdf:li>苏天俊宇</rdf:li></rdf:Seq></dc:creator>
      </rdf:Description>
     </rdf:RDF>
    </x:xmpmeta>
    <?xpacket end="w"?>
    """

    func testXMPReaderParsesCommonFields() throws {
        let metadata = try XCTUnwrap(XMPReader.parse(Data(Self.sampleXMP.utf8)))
        XCTAssertEqual(metadata.rating, 4)
        XCTAssertEqual(metadata.colorLabel, "蓝")
        XCTAssertEqual(metadata.city, "上海")
        XCTAssertEqual(metadata.country, "中国")
        XCTAssertEqual(metadata.province, "上海市")
        XCTAssertEqual(metadata.title, "外滩夜色")
        XCTAssertEqual(metadata.caption, "黄浦江对岸")
        XCTAssertEqual(metadata.creator, "苏天俊宇")
        XCTAssertEqual(metadata.keywords, ["城市夜景", "陆家嘴"])
        XCTAssertNil(XMPReader.parse(Data("<not-xmp/>".utf8)))
    }

    func testImporterAppliesXMPAndKeepsUserEdits() throws {
        let root = try makeImportDirectory()
        let sidecar = root.appendingPathComponent("XMP_0001.xmp")
        try Self.sampleXMP.write(to: sidecar, atomically: true, encoding: .utf8)

        let first = try Importer.importFolder(root, generateThumbnails: false)
        XCTAssertEqual(first.added, 1)
        XCTAssertEqual(first.xmpApplied, 1, "首次导入应回填 XMP 字段")

        let catalog = try Catalog(directory: root)
        var asset = try XCTUnwrap(try catalog.loadAssets(limit: 10).first)
        XCTAssertEqual(asset.rating, 4)
        XCTAssertEqual(asset.city, "上海")
        XCTAssertEqual(asset.colorLabel, "蓝")
        XCTAssertEqual(Set(asset.keywords), ["城市夜景", "陆家嘴"])
        XCTAssertEqual(asset.title, "外滩夜色")

        // 用户手动改评分/城市/主题后，再导入不应被 XMP 覆盖
        var patch = AssetPatch()
        patch.rating = 5
        patch.city = .set("北京")
        try catalog.update(patch: patch, ids: [asset.id])

        // 让文件“看起来变了”，触发 update 分支
        let modified = Date().addingTimeInterval(60)
        try FileManager.default.setAttributes([.modificationDate: modified],
                                              ofItemAtPath: root.appendingPathComponent("XMP_0001.jpg").path)

        let second = try Importer.importFolder(root, generateThumbnails: false)
        XCTAssertEqual(second.updated, 1)
        let refreshed = try XCTUnwrap(try catalog.loadAssets(limit: 10).first)
        XCTAssertEqual(refreshed.rating, 5, "用户评分必须保留")
        XCTAssertEqual(refreshed.city, "北京", "用户城市必须保留")
        XCTAssertEqual(Set(refreshed.keywords), ["城市夜景", "陆家嘴"], "关键词只回填空字段")
        XCTAssertEqual(second.xmpApplied, 0, "没有可回填的空字段时不应计数")
    }

    func testImporterWithoutSidecarLeavesOptionalFieldsEmpty() throws {
        let root = try makeImportDirectory()
        let summary = try Importer.importFolder(root, generateThumbnails: false)
        XCTAssertEqual(summary.added, 1)
        XCTAssertEqual(summary.xmpApplied, 0)
        let catalog = try Catalog(directory: root)
        let asset = try XCTUnwrap(try catalog.loadAssets(limit: 10).first)
        XCTAssertEqual(asset.rating, 0)
        XCTAssertTrue(asset.city?.isEmpty ?? true)
        XCTAssertTrue(asset.keywords.isEmpty)
    }
}


// MARK: - 多文件拖拽载荷

final class DragPayloadTests: XCTestCase {
    func testDragsWholeSelectionWhenCardIsSelected() {
        let ids = DragPayload.assetIDs(dragging: "b", selection: ["a", "b", "c"])
        XCTAssertEqual(ids, ["a", "b", "c"])
    }

    func testDragsSingleCardWhenNotSelected() {
        let ids = DragPayload.assetIDs(dragging: "z", selection: ["a", "b"])
        XCTAssertEqual(ids, ["z"])
    }

    func testDragOrderIsStable() {
        XCTAssertEqual(DragPayload.assetIDs(dragging: "b", selection: ["c", "a", "b"]),
                       ["a", "b", "c"])
    }
}
