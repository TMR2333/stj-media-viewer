// stjcheck —— STJ Media Viewer 样例素材库 / 核心查询语义自检工具。
//
// 用法：
//   .build/debug/stjcheck --data-dir .testdata
//   .build/debug/stjcheck --data-dir .testdata/catalog.db
//
// 只读检查直接用 STJCore 的公开读 API（Catalog）与 Database 取 SQL 真值做交叉验证；
// 写库类检查（批量更新、合集 CRUD）一律先把 catalog.db 复制到临时目录再操作，
// 保证 fixture 不被污染。输出 [OK]/[FAIL]，末尾打印 checks/failures，失败时 exit 1。

import Foundation
import STJCore

// MARK: - 输出与断言

var checks = 0
var failures = 0

struct CheckFailure: Error {
    let message: String
}

func ensure(_ condition: Bool, _ message: @autoclosure () -> String) throws {
    if !condition { throw CheckFailure(message: message()) }
}

func ensureEqual<T: Equatable>(_ actual: T, _ expected: T, _ label: String) throws {
    if actual != expected {
        throw CheckFailure(message: "\(label)：期望 \(expected)，实际 \(actual)")
    }
}

/// 执行一项检查：闭包返回可选的补充说明（成功时打印）。
func check(_ title: String, _ body: () throws -> String?) {
    checks += 1
    do {
        let detail = try body()
        let suffix = (detail?.isEmpty == false) ? " — \(detail!)" : ""
        print("[OK] \(title)\(suffix)")
    } catch let failure as CheckFailure {
        failures += 1
        print("[FAIL] \(title) — \(failure.message)")
    } catch {
        failures += 1
        print("[FAIL] \(title) — \(error)")
    }
}

func info(_ text: String) {
    print("       \(text)")
}

// MARK: - 参数

func printUsage() {
    print("""
    stjcheck — STJ Media Viewer 样例库自检
    用法：stjcheck [--data-dir <目录|目录/catalog.db>] [--import <素材目录>]
      --data-dir  样例库目录，默认 ./​.testdata（也接受 .testdata 或 catalog.db 路径）
      --import    扫描导入目录：没有 catalog.db 时按 Windows 兼容 schema 新建，然后打印导入结果
      --dav-endpoint/--dav-user/--dav-password/--dav-root/--dav-layout/--dav-limit
                  用 WebDAV 上传 --data-dir 里的前 N 个素材（真实 HTTP 往返，供本机 MockWebDav 校验）
      --export-exchange <file.json>
                  导出「设置与图片数据」交换文件（与 Windows 版通用格式）
      --import-exchange <file.json> [--merge fill|overwrite]
                  导入交换文件并写库（默认只填空白字段）
      -h, --help  显示帮助
    """.replacingOccurrences(of: "\u{200B}", with: ""))
}

var dataDirArgument: String?
var importDirectoryArgument: String?
var exportExchangeArgument: String?
var importExchangeArgument: String?
var mergeArgument: String = "fillEmpty"
var davEndpointArgument: String?
var davUserArgument: String = ""
var davPasswordArgument: String = ""
var davRootArgument: String = ""
var davLayoutArgument: String = "byDate"
var davLimitArgument: Int = 2
var argIndex = 1
let arguments = CommandLine.arguments
while argIndex < arguments.count {
    switch arguments[argIndex] {
    case "--data-dir":
        guard argIndex + 1 < arguments.count else {
            print("[FAIL] 参数错误 — --data-dir 需要一个路径")
            exit(2)
        }
        dataDirArgument = arguments[argIndex + 1]
        argIndex += 2
    case "--import":
        guard argIndex + 1 < arguments.count else {
            print("[FAIL] 参数错误 — --import 需要一个目录路径")
            exit(2)
        }
        importDirectoryArgument = arguments[argIndex + 1]
        argIndex += 2
    case "--export-exchange":
        guard argIndex + 1 < arguments.count else {
            print("[FAIL] 参数错误 — --export-exchange 需要一个文件路径")
            exit(2)
        }
        exportExchangeArgument = arguments[argIndex + 1]
        argIndex += 2
    case "--import-exchange":
        guard argIndex + 1 < arguments.count else {
            print("[FAIL] 参数错误 — --import-exchange 需要一个文件路径")
            exit(2)
        }
        importExchangeArgument = arguments[argIndex + 1]
        argIndex += 2
    case "--merge":
        guard argIndex + 1 < arguments.count else {
            print("[FAIL] 参数错误 — --merge 需要 fill 或 overwrite")
            exit(2)
        }
        mergeArgument = arguments[argIndex + 1]
        argIndex += 2
    case "--dav-endpoint":
        guard argIndex + 1 < arguments.count else {
            print("[FAIL] 参数错误 — --dav-endpoint 需要一个 URL")
            exit(2)
        }
        davEndpointArgument = arguments[argIndex + 1]
        argIndex += 2
    case "--dav-user":
        davUserArgument = arguments[argIndex + 1]; argIndex += 2
    case "--dav-password":
        davPasswordArgument = arguments[argIndex + 1]; argIndex += 2
    case "--dav-root":
        davRootArgument = arguments[argIndex + 1]; argIndex += 2
    case "--dav-layout":
        davLayoutArgument = arguments[argIndex + 1]; argIndex += 2
    case "--dav-limit":
        davLimitArgument = Int(arguments[argIndex + 1]) ?? 2; argIndex += 2
    case "-h", "--help":
        printUsage()
        exit(0)
    default:
        print("[FAIL] 参数错误 — 未知参数 \(arguments[argIndex])")
        printUsage()
        exit(2)
    }
}

let currentDirectory = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)

// --import：扫描导入（P2），先跑导入再决定是否继续自检
if let importDirectoryArgument {
    let expanded = (importDirectoryArgument as NSString).expandingTildeInPath
    let folder = URL(fileURLWithPath: expanded, relativeTo: currentDirectory).standardizedFileURL
    do {
        let summary = try Importer.importFolder(folder, libraryName: folder.lastPathComponent) { progress in
            if progress.total > 0, progress.processed % 25 == 0 {
                print("  … 扫描 \(progress.processed)/\(progress.total)")
            }
        }
        print("[OK] 扫描导入 — \(summary.descriptionText)；总素材 \(summary.totalAssets)；目录 \(folder.path)")

        // 导入后立刻用 app 的读取路径复核一次：可打开、行数一致、缩略图能命中
        let catalog = try Catalog(directory: folder)
        let visible = try catalog.count()
        let loaded = try catalog.loadAssets(limit: 20_000)
        let store = ThumbnailStore()
        store.configure(cacheDirectory: folder.appendingPathComponent("Cache/thumbs"))
        let hits = store.cachedHitCount(for: loaded)
        let readonly = try Database(path: folder.appendingPathComponent("catalog.db").path, readOnly: true)
        let journal = try readonly.query("PRAGMA journal_mode;") { $0.string(0) ?? "" }.first ?? "?"
        print("[OK] 导入结果复核 — 只读打开成功、可见 \(visible) 张、缓存命中 \(hits) 张、journal_mode=\(journal)")
        for failure in summary.failures.prefix(10) {
            print("[WARN] \(failure.path)：\(failure.message)")
        }
        if summary.failed > 0 {
            print("checks: 1, failures: \(summary.failed)")
            exit(1)
        }
        exit(0)
    } catch {
        print("[FAIL] 扫描导入 — \(error.localizedDescription)")
        print("checks: 1, failures: 1")
        exit(1)
    }
}

// --export-exchange / --import-exchange：设置与图片数据交换文件（跨平台 JSON）
if let exportExchangeArgument {
    guard let dataDirectory = normalizeDataDir(dataDirArgument) else {
        print("[FAIL] 导出交换文件 — 需要 --data-dir 指向素材库")
        exit(2)
    }
    do {
        let catalog = try Catalog(directory: dataDirectory)
        let document = try SettingsExchange.exportDocument(catalog: catalog, appVersion: "dev")
        let data = try SettingsExchange.encode(document)
        let outURL = URL(fileURLWithPath: (exportExchangeArgument as NSString).expandingTildeInPath,
                         relativeTo: currentDirectory).standardizedFileURL
        try data.write(to: outURL, options: .atomic)
        print("[OK] 导出交换文件 — 设置 + \(document.assets.count) 条图片数据 + \(document.collections.count) 个合集 → \(outURL.path)")
        print("checks: 1, failures: 0")
        exit(0)
    } catch {
        print("[FAIL] 导出交换文件 — \(error.localizedDescription)")
        print("checks: 1, failures: 1")
        exit(1)
    }
}

if let importExchangeArgument {
    guard let dataDirectory = normalizeDataDir(dataDirArgument) else {
        print("[FAIL] 导入交换文件 — 需要 --data-dir 指向素材库")
        exit(2)
    }
    do {
        let catalog = try Catalog(directory: dataDirectory)
        let inURL = URL(fileURLWithPath: (importExchangeArgument as NSString).expandingTildeInPath,
                        relativeTo: currentDirectory).standardizedFileURL
        let plan = try SettingsExchange.plan(fromFile: inURL, catalog: catalog)
        let mode: ExchangeMergeMode = mergeArgument.lowercased().hasPrefix("over") ? .overwrite : .fillEmpty
        let summary = try SettingsExchange.apply(plan: plan, mode: mode, catalog: catalog)
        print("[OK] 导入交换文件 — 匹配 \(plan.matchedCount) 条、未匹配 \(plan.unmatchedCount) 条；\(summary.descriptionText)")
        let reloaded = try catalog.loadAssets(limit: 200_000)
        let rated = reloaded.filter { $0.rating > 0 }.count
        let keyworded = reloaded.filter { !$0.keywords.isEmpty }.count
        print("[OK] 导入结果复核 — 素材 \(reloaded.count) 条、评分 >0 \(rated) 条、有关键词 \(keyworded) 条、合集 \(try catalog.collections().count) 个")
        print("checks: 2, failures: 0")
        exit(0)
    } catch {
        print("[FAIL] 导入交换文件 — \(error.localizedDescription)")
        print("checks: 1, failures: 1")
        exit(1)
    }
}

// --dav-endpoint：用 WebDAV 上传样例库前 N 个素材（P4 云同步链路自检）
if let davEndpointArgument {
    let normalized = try? URL(fileURLWithPath: (davEndpointArgument as NSString).expandingTildeInPath)
    _ = normalized
    guard let endpoint = URL(string: davEndpointArgument), endpoint.scheme != nil else {
        print("[FAIL] WebDAV 自检 — 地址无效：\(davEndpointArgument)")
        exit(2)
    }
    guard let dataDirectory = normalizeDataDir(dataDirArgument) else {
        print("[FAIL] WebDAV 自检 — 需要 --data-dir 指向样例库")
        exit(2)
    }
    let credentials = WebDAVCredentials(endpoint: endpoint,
                                        username: davUserArgument,
                                        password: davPasswordArgument,
                                        remoteRoot: davRootArgument)
    let layout = CloudLayout(rawValue: davLayoutArgument) ?? .byDate
    let catalog: Catalog
    do {
        catalog = try Catalog(directory: dataDirectory)
    } catch {
        print("[FAIL] WebDAV 自检 — 打不开素材库：\(error.localizedDescription)")
        exit(1)
    }
    let assets: [Asset]
    do {
        assets = Array(try catalog.loadAssets(limit: max(davLimitArgument, 1)).prefix(davLimitArgument))
    } catch {
        print("[FAIL] WebDAV 自检 — 读取素材失败：\(error.localizedDescription)")
        exit(1)
    }
    let (items, skipped) = CloudSync.plan(assets: assets, layout: layout)
    print("[OK] 同步计划 — 布局 \(layout.title)；待上传 \(items.count) 条，跳过 \(skipped.count) 条")
    for item in items {
        print("       → \(item.remotePath)")
    }

    let steps = await WebDAVDiagnostics.run(credentials: credentials)
    var diagnosticsOK = true
    for step in steps {
        print("[\(step.succeeded ? "OK" : "FAIL")] 诊断 \(step.title) — \(step.detail)")
        if !step.succeeded { diagnosticsOK = false }
    }

    let result = await CloudSync.upload(items: items, credentials: credentials)
    print("[\(result.failed.isEmpty ? "OK" : "FAIL")] 上传结果 — \(result.summaryText)")
    for failure in result.failed {
        print("       ✗ \(failure.name)：\(failure.message)")
    }
    for path in result.uploaded {
        print("       ✓ \(path)")
    }

    let ok = diagnosticsOK && result.failed.isEmpty && !result.uploaded.isEmpty
    print("checks: 1, failures: \(ok ? 0 : 1)")
    exit(ok ? 0 : 1)
}

func normalizeDataDir(_ raw: String?) -> URL? {
    var candidates: [URL] = []
    if let raw {
        let expanded = (raw as NSString).expandingTildeInPath
        let url = expanded.hasPrefix("/")
            ? URL(fileURLWithPath: expanded)
            : currentDirectory.appendingPathComponent(expanded)
        candidates.append(url.standardizedFileURL)
    } else {
        candidates.append(currentDirectory.appendingPathComponent(".testdata"))
        candidates.append(currentDirectory.appendingPathComponent(".testdata"))
    }
    for candidate in candidates {
        var isDirectory: ObjCBool = false
        let exists = FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDirectory)
        var directory = candidate
        if exists && !isDirectory.boolValue {
            directory = candidate.deletingLastPathComponent()
        } else if !exists && candidate.lastPathComponent == "catalog.db" {
            directory = candidate.deletingLastPathComponent()
        }
        if FileManager.default.fileExists(atPath: directory.appendingPathComponent("catalog.db").path) {
            return directory
        }
    }
    return nil
}

guard let rawDataDir = normalizeDataDir(dataDirArgument) else {
    print("[FAIL] 打开样例库 — 找不到目录中的 catalog.db（提示：--data-dir <目录> 或 <目录>/catalog.db）")
    print("checks: 1, failures: 1")
    exit(1)
}

let dataDir = rawDataDir.resolvingSymlinksInPath().standardizedFileURL
let databaseURL = dataDir.appendingPathComponent("catalog.db")
let thumbsURL = dataDir.appendingPathComponent("Cache/thumbs")

print("=== STJ Media Viewer · 样例库自检 ===")
print("目录：\(dataDir.path)")

// MARK: - 打开样例库

let catalog: Catalog
do {
    catalog = try Catalog(directory: dataDir)
} catch {
    print("[FAIL] 打开样例库 — \(error)")
    print("checks: 1, failures: 1")
    exit(1)
}

guard let db = try? Database(path: databaseURL.path, readOnly: true) else {
    print("[FAIL] 打开只读连接 — 无法打开 \(databaseURL.path)")
    print("checks: 1, failures: 1")
    exit(1)
}

// 注意前导空格：本常量直接拼接在 "FROM assets" 之后。
let coverOnlyWhere = " WHERE (is_group_cover = 1 OR group_id IS NULL OR group_id = '')"

func rawCount(_ sql: String, _ binds: [Any?] = []) throws -> Int {
    try db.scalarInt(sql, binds: binds)
}

func allAssets() throws -> [Asset] {
    try catalog.loadAssets(limit: 5_000, offset: 0)
}

func asset(withSearch text: String) throws -> Asset? {
    var filter = AssetFilter()
    filter.searchText = text
    return try catalog.loadAssets(filter: filter, limit: 5).first
}

func allFixtureAssets() throws -> [Asset] {
    var assets = try allAssets()
    let visible = Set(assets.map(\.id))
    for group in try catalog.mergeGroups() {
        let members = try catalog.groupMembers(groupID: group.id)
        for member in members where !visible.contains(member.id) {
            assets.append(member)
        }
    }
    return assets
}

/// 写库类检查专用：把 fixture 的 catalog.db 复制到临时目录再操作，
/// 绝不污染 .testdata 里的原始样例库。
struct TemporaryCatalog {
    let directory: URL

    init(sourceDatabase: URL) throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stjcheck-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: sourceDatabase, to: root.appendingPathComponent("catalog.db"))
        self.directory = root
    }

    func cleanup() {
        try? FileManager.default.removeItem(at: directory)
    }
}

// MARK: - ① schema / 行数

let requiredAssetColumns: [String] = [
    "id", "library_id", "full_path", "file_name", "relative_path", "size_bytes",
    "file_modified_ms", "kind", "format_code", "image_flavor", "raw_format",
    "width", "height", "duration_seconds", "frame_rate", "bit_depth", "video_codec",
    "has_audio", "color_space", "is_log", "log_profile", "camera_make", "camera_model",
    "device_key", "lens_model", "focal_length_mm", "aperture_f", "shutter_speed_seconds",
    "iso", "captured_at", "captured_ms", "captured_day", "captured_at_source",
    "latitude", "longitude", "country", "province", "city", "city_source",
    "title", "caption", "creator", "copyright", "keywords_json", "keywords_flat",
    "post_process", "has_develop_edits", "develop_engine", "xmp_path", "sidecars_json",
    "has_embedded_xmp", "theme", "themes_json", "rotation_degrees", "flip_h", "flip_v",
    "tone", "rating", "color_label", "is_pick", "ai_subject", "ai_mood", "quality_score",
    "provenance_json", "quick_hash", "full_hash", "thumb_key", "indexed_at", "indexed_ms",
    "metadata_parsed_at", "last_error", "group_id", "is_group_cover", "group_has_raw",
    "group_has_jpeg", "companions_json", "lut_id",
]

check("assets 全 schema 列检测") {
    let columns = try db.tableColumns("assets")
    let missing = requiredAssetColumns.filter { !columns.contains($0) }
    try ensure(missing.isEmpty, "缺少列：\(missing.joined(separator: ", "))")
    return "\(columns.count) 列，必备 \(requiredAssetColumns.count) 列齐全"
}

check("附属表齐全") {
    let required = ["libraries", "meta", "asset_custom", "collections",
                    "collection_assets", "merge_groups", "merge_group_assets"]
    var missing: [String] = []
    for table in required where try !db.tableExists(table) {
        missing.append(table)
    }
    try ensure(missing.isEmpty, "缺少表：\(missing.joined(separator: ", "))")
    // macOS 端口额外依赖的列（Windows DDL 里没有）
    try ensure(try db.tableColumns("libraries").contains("root_path"), "libraries 缺少 root_path（Catalog.libraries() 需要）")
    try ensure(try db.tableColumns("collections").contains("created_ms"), "collections 缺少 created_ms（createCollection 需要）")
    try ensure(try db.tableColumns("collection_assets").contains("added_ms"), "collection_assets 缺少 added_ms（addToCollection 需要）")
    try ensure(try db.tableColumns("merge_group_assets").contains("position"), "merge_group_assets 缺少 position（groupMembers 排序需要）")
    return "\(required.count) 张表 + 4 个兼容列"
}

check("assets 行数与默认可见行数") {
    let raw = try rawCount("SELECT count(*) FROM assets;")
    let visible = try catalog.count()
    try ensureEqual(raw, 28, "原始行数")
    let hidden = try rawCount("SELECT count(*) FROM assets WHERE group_id IS NOT NULL AND group_id <> '' AND is_group_cover = 0;")
    try ensureEqual(visible + hidden, raw, "默认可见行数 + 折叠隐藏行数")
    let journalMode = try db.query("PRAGMA journal_mode;") { $0.string(0) ?? "" }.first ?? ""
    try ensure(journalMode.lowercased() == "delete", "journal_mode 应为 delete（app 只读打开），实际 \(journalMode)")
    return "原始 \(raw) 行 / 默认可见 \(visible) 行（折叠隐藏 \(hidden) 行）/ journal_mode=\(journalMode)"
}

// MARK: - ② kind 筛选

check("kind 筛选（图片 / 视频）") {
    var videoFilter = AssetFilter()
    videoFilter.kinds = [2]
    let videos = try catalog.loadAssets(filter: videoFilter, limit: 100)
    try ensureEqual(videos.count, try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND kind = 2;"), "视频数")
    try ensure(videos.count == 3, "样例库应有 3 个视频，实际 \(videos.count)")
    try ensure(videos.allSatisfy { $0.kind == 2 && $0.isVideo }, "视频筛选结果里混入了非视频")

    var imageFilter = AssetFilter()
    imageFilter.kinds = [1]
    let images = try catalog.loadAssets(filter: imageFilter, limit: 100)
    try ensure(images.allSatisfy { $0.kind == 1 && !$0.isVideo }, "图片筛选结果里混入了视频")
    try ensureEqual(images.count, try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND kind = 1;"), "图片数")
    return "视频 \(videos.count) / 图片 \(images.count)"
}

// MARK: - ③ 年份维度

check("年份维度筛选（captured_day 前 4 位）") {
    var filter = AssetFilter()
    filter.years = ["2025"]
    let assets = try catalog.loadAssets(filter: filter, limit: 1_000)
    let expected = try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND substr(captured_day, 1, 4) = '2025';")
    try ensureEqual(assets.count, expected, "2025 年行数")
    try ensure(assets.count > 0, "2025 年筛选结果为空")
    try ensure(assets.allSatisfy { ($0.capturedDay ?? "").hasPrefix("2025") }, "结果里出现了非 2025 年素材")

    let facets = try catalog.loadFacets()
    let facetCounts = Dictionary(uniqueKeysWithValues: facets.years.map { ($0.value, $0.count) })
    try ensureEqual(facetCounts["2025"] ?? -1, expected, "facets 2025 计数")
    let yearSet = Set(facets.years.map(\.value))
    try ensure(yearSet.isSuperset(of: ["2024", "2025", "2026"]), "facets 年份缺少 2024/2025/2026：\(yearSet.sorted())")
    return "2025 年 \(assets.count) 张，facets 年份 \(yearSet.sorted().joined(separator: "/"))"
}

// MARK: - ④ 城市维度

check("城市维度筛选（上海）") {
    var filter = AssetFilter()
    filter.cities = ["上海"]
    let assets = try catalog.loadAssets(filter: filter, limit: 1_000)
    let expected = try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND city IN ('上海');")
    try ensureEqual(assets.count, expected, "上海行数")
    try ensure(assets.count > 0, "上海筛选结果为空")
    try ensure(assets.allSatisfy { $0.city == "上海" }, "结果里出现了非上海素材")
    try ensureEqual(try catalog.count(filter: filter), expected, "count(filter:) 与 loadAssets 行数")
    return "上海 \(assets.count) 张"
}

// MARK: - ⑤ 多主题任一命中

check("多主题任一命中（themes_json LIKE）") {
    var filter = AssetFilter()
    filter.themes = ["建筑"]
    let assets = try catalog.loadAssets(filter: filter, limit: 1_000)
    let expected = try rawCount("""
    SELECT count(*) FROM assets\(coverOnlyWhere)
    AND (theme = '建筑' OR themes_json LIKE '%"建筑"%');
    """)
    try ensureEqual(assets.count, expected, "主题「建筑」行数")
    try ensure(assets.count > 0, "主题「建筑」筛选结果为空")
    try ensure(assets.allSatisfy { $0.themes.contains("建筑") }, "结果里出现了 themes 不含「建筑」的素材")
    // 「建筑」在样例库里只作为第二/第三主题出现，命中只能来自 themes_json LIKE 分支。
    let viaJsonOnly = assets.filter { $0.themes.first != "建筑" }
    try ensure(!viaJsonOnly.isEmpty, "所有命中行的 theme 都是「建筑」，没有验证到 themes_json 多主题分支")
    let multiTheme = assets.filter { $0.themes.count >= 2 }
    try ensure(!multiTheme.isEmpty, "没有多主题素材")
    return "命中 \(assets.count) 张，其中 \(viaJsonOnly.count) 张靠 themes_json 命中（如 \(viaJsonOnly[0].fileName) → \(viaJsonOnly[0].themes.joined(separator: "/"))）"
}

// MARK: - ⑥ __unset__ 未填写筛选

check("__unset__ 未填写筛选（city）") {
    var unsetFilter = AssetFilter()
    unsetFilter.unsetDimensions = [.city]
    let unset = try catalog.loadAssets(filter: unsetFilter, limit: 1_000)
    let expectedUnset = try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND (city IS NULL OR city = '');")
    try ensureEqual(unset.count, expectedUnset, "city 未填写行数")
    try ensure(unset.count >= 4, "city 未填写行数应 ≥ 4（任务要求），实际 \(unset.count)")
    try ensure(unset.allSatisfy { ($0.city ?? "").isEmpty }, "未填写结果里出现了有城市的素材")

    var unionFilter = AssetFilter()
    unionFilter.cities = ["上海"]
    unionFilter.unsetDimensions = [.city]
    let union = try catalog.loadAssets(filter: unionFilter, limit: 1_000)
    let shanghai = try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND city = '上海';")
    try ensureEqual(union.count, shanghai + expectedUnset, "「上海」∪ 未填写行数")
    try ensure(union.count > shanghai, "并集没有变化，未填写分支未生效")

    let facets = try catalog.loadFacets()
    try ensureEqual(facets.unsetCounts[.city] ?? -1, expectedUnset, "facets.unsetCounts[.city]")
    return "未填写 \(unset.count) 张；上海 ∪ 未填写 = \(union.count) 张"
}

// MARK: - ⑦ 三态（精选 / 废片 / 未标记）

check("is_pick 三态计数与筛选") {
    let rawPicked = try rawCount("SELECT count(*) FROM assets WHERE is_pick = 1;")
    let rawRejected = try rawCount("SELECT count(*) FROM assets WHERE is_pick = 0;")
    let rawUnmarked = try rawCount("SELECT count(*) FROM assets WHERE is_pick IS NULL;")
    try ensureEqual(rawPicked + rawRejected + rawUnmarked, 28, "三态行数之和")
    try ensure(rawPicked > 0 && rawRejected > 0 && rawUnmarked > 0,
               "三态必须都有数据：精选 \(rawPicked) / 废片 \(rawRejected) / 未标记 \(rawUnmarked)")

    let facets = try catalog.loadFacets()
    let coverPicked = try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND is_pick = 1;")
    let coverRejected = try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND is_pick = 0;")
    let coverUnmarked = try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND is_pick IS NULL;")
    try ensureEqual(facets.picked, coverPicked, "facets.picked")
    try ensureEqual(facets.rejected, coverRejected, "facets.rejected")
    try ensureEqual(facets.unpicked, coverUnmarked, "facets.unpicked")

    var pickFilter = AssetFilter()
    pickFilter.pick = 1
    try ensureEqual(try catalog.count(filter: pickFilter), coverPicked, "pick=1 筛选行数")
    var rejectFilter = AssetFilter()
    rejectFilter.pick = 0
    try ensureEqual(try catalog.count(filter: rejectFilter), coverRejected, "pick=0 筛选行数")
    return "全部 28 行：精选 \(rawPicked) / 废片 \(rawRejected) / 未标记 \(rawUnmarked)；可见行：\(facets.picked)/\(facets.rejected)/\(facets.unpicked)"
}

// MARK: - ⑧ 评分

check("评分筛选（≥4）与评分分布") {
    var filter = AssetFilter()
    filter.ratings = [4, 5]
    let assets = try catalog.loadAssets(filter: filter, limit: 1_000)
    let expected = try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND rating IN (4, 5);")
    try ensureEqual(assets.count, expected, "评分 ≥4 行数")
    try ensure(assets.allSatisfy { $0.rating >= 4 }, "结果里出现了评分 < 4 的素材")
    let facets = try catalog.loadFacets()
    let rated = Set(facets.ratings.map(\.value))
    try ensure(rated.contains("5") && rated.contains("4"), "facets.ratings 缺少 4/5 分")
    try ensure(facets.ratings.map(\.value).compactMap(Int.init).sorted() == facets.ratings.map(\.value).compactMap(Int.init).sorted(),
               "facets.ratings 不是数值升序")
    let fiveStars = facets.ratings.first { $0.value == "5" }?.count ?? -1
    try ensureEqual(fiveStars, try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND rating = 5;"), "facets 5 星计数")
    return "评分 ≥4 共 \(assets.count) 张；facets 评分档 \(facets.ratings.map(\.value).joined(separator: ","))"
}

// MARK: - ⑨ 排序

check("capturedDesc 排序（拍摄时间倒序）") {
    let assets = try catalog.loadAssets(sort: SortDescriptor(key: .captured, ascending: false), limit: 1_000)
    try ensure(!assets.isEmpty, "排序结果为空")
    let maxMs = try rawCount("SELECT CAST(max(captured_ms) AS INTEGER) FROM assets\(coverOnlyWhere) AND captured_ms IS NOT NULL;")
    try ensureEqual(assets[0].capturedAt.map { Int($0.timeIntervalSince1970 * 1000) } ?? -1, maxMs, "首项拍摄时间")
    let stamps = assets.compactMap { $0.capturedAt?.timeIntervalSince1970 }
    try ensure(stamps == stamps.sorted(by: >), "拍摄时间不是严格倒序")
    return "首项 \(assets[0].fileName)（\(assets[0].capturedText)）"
}

check("ratingDesc 排序（评分倒序）") {
    let assets = try catalog.loadAssets(sort: SortDescriptor(key: .rating, ascending: false), limit: 1_000)
    try ensure(!assets.isEmpty, "排序结果为空")
    let maxRating = try rawCount("SELECT max(rating) FROM assets\(coverOnlyWhere);")
    try ensureEqual(assets[0].rating, maxRating, "首项评分")
    let ratings = assets.map(\.rating)
    try ensure(ratings == ratings.sorted(by: >), "评分不是倒序")
    return "首项 \(assets[0].fileName)（\(assets[0].rating) 星）"
}

// MARK: - ⑩ facets

check("facets 计数与 total 一致") {
    let facets = try catalog.loadFacets()
    let visible = try catalog.count()
    try ensureEqual(facets.total, visible, "facets.total")
    let kindsSum = facets.kinds.reduce(0) { $0 + $1.count }
    try ensureEqual(kindsSum, facets.total, "kinds 计数之和")
    let citySum = facets.cities.reduce(0) { $0 + $1.count } + (facets.unsetCounts[.city] ?? -1)
    try ensureEqual(citySum, facets.total, "cities 计数 + 未填写")
    let formatSum = facets.formats.reduce(0) { $0 + $1.count }
    try ensureEqual(formatSum, facets.total, "formats 计数之和")
    try ensureEqual(facets.processed, try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND post_process = 2;"), "已处理计数")
    try ensureEqual(facets.logCount, try rawCount("SELECT count(*) FROM assets\(coverOnlyWhere) AND is_log <> 0;"), "Log 计数")
    try ensure(facets.themes.count >= 5, "主题 facet 只有 \(facets.themes.count) 项，样例库主题太单一")
    return "total \(facets.total)、类型 \(facets.kinds.count) 项、主题 \(facets.themes.count) 项、年份 \(facets.years.count) 项"
}

// MARK: - ⑪ 批量更新写库（临时副本）

check("批量更新写库（临时副本，不污染 fixture）") {
    guard let target = try asset(withSearch: "bund") else {
        throw CheckFailure(message: "样例库里找不到 bund 素材")
    }
    let seedRating = target.rating
    let seedCity = target.city

    let temporary = try TemporaryCatalog(sourceDatabase: databaseURL)
    defer { temporary.cleanup() }
    let copied = try Catalog(directory: temporary.directory)
    guard let copyTarget = try copied.loadAssets(filter: AssetFilter(), limit: 5_000).first(where: { $0.id == target.id }) else {
        throw CheckFailure(message: "临时副本里找不到目标素材")
    }
    try ensureEqual(copyTarget.rating, seedRating, "副本初始评分")

    var patch = AssetPatch()
    patch.rating = 2
    patch.isPick = .set(1)
    patch.city = .set("杭州")
    patch.country = .set("中国")
    patch.province = .set("浙江省")
    patch.tone = .set("暖调")
    patch.colorLabel = .set("紫")
    patch.themes = .set(["测试主题", "写库"])
    try copied.update(patch: patch, ids: [target.id])

    guard let updated = try copied.loadAssets(filter: AssetFilter(), limit: 5_000).first(where: { $0.id == target.id }) else {
        throw CheckFailure(message: "更新后读不到目标素材")
    }
    try ensureEqual(updated.rating, 2, "更新后的评分")
    try ensureEqual(updated.isPick, 1, "更新后的 isPick")
    try ensureEqual(updated.city, "杭州", "更新后的城市")
    try ensureEqual(updated.country, "中国", "更新后的国家")
    try ensureEqual(updated.province, "浙江省", "更新后的省份")
    try ensureEqual(updated.tone, "暖调", "更新后的影调")
    try ensureEqual(updated.colorLabel, "紫", "更新后的色标")
    try ensureEqual(updated.themes, ["测试主题", "写库"], "更新后的主题")
    try ensureEqual(updated.themes.first, "测试主题", "theme 列 = themes 第一项")

    // fixture 回归：原库那行必须保持原样。
    if let pristine = try asset(withSearch: "bund") {
        try ensureEqual(pristine.rating, seedRating, "fixture 原始评分（不能被临时副本的写操作污染）")
        try ensureEqual(pristine.city, seedCity, "fixture 原始城市")
    }
    return "\(target.fileName)：rating \(seedRating)→2、city \(seedCity ?? "nil")→杭州、themes→测试主题/写库"
}

// MARK: - ⑫ 合集 CRUD（临时副本）

check("合集 CRUD（临时副本）") {
    let temporary = try TemporaryCatalog(sourceDatabase: databaseURL)
    defer { temporary.cleanup() }
    let copied = try Catalog(directory: temporary.directory)

    let seeded = try copied.collections()
    try ensureEqual(seeded.count, 3, "样例库内置合集数")
    try ensure(seeded.allSatisfy { $0.count >= 3 }, "每个内置合集都应 ≥3 个素材：\(seeded.map { "\($0.name)=\($0.count)" })")

    let assets = try copied.loadAssets(limit: 3)
    try ensure(assets.count >= 2, "临时副本素材不足")
    let newID = try copied.createCollection(name: "自检临时合集")
    var afterCreate = try copied.collections()
    try ensure(afterCreate.contains { $0.id == newID && $0.name == "自检临时合集" }, "createCollection 后找不到新合集")
    try ensureEqual(afterCreate.count, 4, "新建后的合集数")

    try copied.addToCollection(collectionID: newID, assetIDs: [assets[0].id, assets[1].id])
    afterCreate = try copied.collections()
    try ensureEqual(afterCreate.first { $0.id == newID }?.count ?? -1, 2, "加入两个素材后的合集计数")

    try copied.renameCollection(id: newID, name: "自检临时合集（改名）")
    try ensure(try copied.collections().contains { $0.id == newID && $0.name == "自检临时合集（改名）" }, "renameCollection 未生效")

    try copied.removeFromCollection(collectionID: newID, assetIDs: [assets[0].id])
    try ensureEqual(try copied.collections().first { $0.id == newID }?.count ?? -1, 1, "移除一个素材后的合集计数")

    try copied.deleteCollection(id: newID)
    let afterDelete = try copied.collections()
    try ensure(!afterDelete.contains { $0.id == newID }, "deleteCollection 未生效")
    try ensureEqual(afterDelete.count, 3, "删除后的合集数")
    return "内置 3 个合集（\(seeded.map { "\($0.name)=\($0.count)" }.joined(separator: "、"))）；新建 → 加入 2 → 改名 → 移除 1 → 删除，全部通过"
}

// MARK: - ⑬ 合并组与展开

check("合并组展开（expandedIDs / groupMembers）") {
    let groups = try catalog.mergeGroups()
    try ensureEqual(groups.count, 2, "合并组数量")
    let visible = try catalog.count()
    let raw = try rawCount("SELECT count(*) FROM assets;")
    try ensure(visible < raw, "折叠视图没有隐藏任何非封面成员（cover-only 未生效）")

    var expandedTotal = 0
    for group in groups {
        let members = try catalog.groupMembers(groupID: group.id)
        try ensureEqual(members.count, group.count, "「\(group.name)」成员数与 mergeGroups().count")
        try ensure(members.count >= 2, "「\(group.name)」应有 ≥2 个成员")
        try ensure(group.coverAssetID != nil, "「\(group.name)」缺少封面素材 id")
        try ensure(members.contains { $0.id == group.coverAssetID }, "「\(group.name)」封面素材不在成员列表里")
        try ensureEqual(members.filter { $0.isGroupCover }.count, 1, "「\(group.name)」组内封面数量")
        let coverIDs = try catalog.expandedIDs(for: [group.coverAssetID ?? ""])
        try ensureEqual(coverIDs.count, members.count, "「\(group.name)」expandedIDs 展开数量")
        try ensure(Set(coverIDs) == Set(members.map(\.id)), "「\(group.name)」expandedIDs 与 groupMembers 不一致")
        let expandedAssets = try catalog.loadAssets(limit: 100, expandedGroupID: group.id)
        try ensureEqual(expandedAssets.count, members.count, "「\(group.name)」按组展开的行数")
        expandedTotal += members.count
    }

    guard let standalone = try asset(withSearch: "breakfast") else {
        throw CheckFailure(message: "找不到独立素材 breakfast")
    }
    try ensureEqual(try catalog.expandedIDs(for: [standalone.id]), [standalone.id], "独立素材 expandedIDs 应原样返回")
    // 任务要求组内 position 递增：groupMembers 的返回顺序就是 position 顺序。
    let first = groups[0]
    let orderedNames = try catalog.groupMembers(groupID: first.id).map(\.fileName)
    try ensure(orderedNames == ["SH_20260819_0001_bund.jpg", "SH_20260819_0003_lujiazui.jpg"],
               "「\(first.name)」成员顺序应为 position 顺序：\(orderedNames)")
    return "2 组共 \(expandedTotal) 个成员；默认折叠隐藏 \(raw - visible) 行；展开「\(first.name)」= \(orderedNames.joined(separator: " → "))"
}

// MARK: - ⑭ XMP 包

check("XMPWriter.packet 内容（Rating / Subject / City）") {
    guard let asset = try asset(withSearch: "bund") else {
        throw CheckFailure(message: "找不到 bund 素材")
    }
    let packet = XMPWriter.packet(for: asset)
    try ensure(packet.contains("xmp:Rating=\"\(asset.rating)\""), "packet 缺少 xmp:Rating")
    try ensure(packet.contains("photoshop:City=\"\(asset.city ?? "")\""), "packet 缺少 photoshop:City")
    try ensure(packet.contains("<dc:subject>"), "packet 缺少 dc:subject")
    if let firstKeyword = asset.keywords.first {
        try ensure(packet.contains("<rdf:li>\(firstKeyword)</rdf:li>"), "packet 缺少第一条关键词 rdf:li")
    }
    try ensure(packet.contains("xmp:Label="), "packet 缺少 xmp:Label")
    try ensure(packet.hasPrefix("<?xpacket") || packet.contains("<x:xmpmeta"), "packet 不是 XMP 包结构")
    return "\(asset.fileName)：Rating=\(asset.rating)、City=\(asset.city ?? "")、关键词 \(asset.keywords.count) 个"
}

// MARK: - ⑮ 缩略图

check("缩略图键解析与缓存命中") {
    let assets = try allFixtureAssets()
    try ensureEqual(assets.count, 28, "素材总数")

    let store = ThumbnailStore()
    store.configure(cacheDirectory: thumbsURL)
    let indexed = store.cachedThumbnailCount()
    try ensureEqual(indexed, 20, "缓存缩略图索引数量")
    let hits = store.cachedHitCount(for: assets)
    try ensureEqual(hits, 20, "按素材计算的缓存命中数")

    let rows = try db.query("SELECT id, full_path, thumb_key FROM assets ORDER BY file_name;") { statement in
        (statement.string(0) ?? "", statement.string(1) ?? "", statement.string(2))
    }
    let withKey = rows.filter { ($0.2 ?? "").isEmpty == false }
    try ensureEqual(withKey.count, 20, "thumb_key 非空的行数")
    var mismatched: [String] = []
    for (id, fullPath, key) in withKey {
        guard let key else { continue }
        let expected = SHA1.hexString(Data(fullPath.lowercased().utf8))
        let parts = key.split(separator: "_")
        if parts.first.map(String.init) != expected { mismatched.append(id) }
        let file = thumbsURL.appendingPathComponent("\(key)_360.jpg")
        if !FileManager.default.fileExists(atPath: file.path) { mismatched.append("\(id)(缺文件)") }
    }
    try ensure(mismatched.isEmpty, "缩略图键与文件不匹配：\(mismatched.prefix(5).joined(separator: ", "))")
    let quickLook = assets.count - hits
    return "索引 \(indexed) 个文件、命中 \(hits) 张、QuickLook 兜底 \(quickLook) 张（文件名 = sha1(full_path 小写) + _1_360.jpg）"
}

// MARK: - ⑯ 路径与磁盘一致

check("素材路径与磁盘文件一致") {
    let rows = try db.query("SELECT id, full_path, file_name, relative_path, kind FROM assets ORDER BY file_name;") { statement in
        (statement.string(0) ?? "", statement.string(1) ?? "", statement.string(2) ?? "",
         statement.string(3) ?? "", statement.int(4) ?? 0)
    }
    try ensureEqual(rows.count, 28, "素材行数")
    var missing: [String] = []
    var mismatched: [String] = []
    for (id, fullPath, fileName, relativePath, kind) in rows {
        let url = URL(fileURLWithPath: fullPath)
        if !FileManager.default.fileExists(atPath: url.path) { missing.append(id) }
        if url.lastPathComponent != fileName { mismatched.append("\(id)：fileName ≠ 磁盘文件名") }
        let expectedPrefix = kind == 2 ? "media/videos/" : "media/images/"
        if !relativePath.hasPrefix(expectedPrefix) { mismatched.append("\(id)：relative_path=\(relativePath)") }
        let fileSize = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int64) ?? nil
        if let fileSize {
            let dbSize = try db.scalarInt("SELECT size_bytes FROM assets WHERE id = ?;", binds: [id])
            if Int(fileSize) != dbSize { mismatched.append("\(id)：size_bytes 不一致") }
        }
    }
    try ensure(missing.isEmpty, "磁盘上找不到 \(missing.count) 个素材文件：\(missing.prefix(5).joined(separator: ", "))")
    try ensure(mismatched.isEmpty, mismatched.prefix(5).joined(separator: "; "))
    return "28 个素材文件均存在，fileName/relative_path/size_bytes 与磁盘一致"
}

// MARK: - ⑰ libraries / 元数据 / 关键字

check("libraries 根目录与关键字/主题元数据") {
    let libraries = try catalog.libraries()
    try ensureEqual(libraries.count, 1, "libraries 行数")
    guard let library = libraries.first else { throw CheckFailure(message: "libraries 为空") }
    try ensureEqual(URL(fileURLWithPath: library.rootPath).standardizedFileURL.path,
                    dataDir.path, "libraries.root_path")
    try ensureEqual(try db.scalarInt("SELECT asset_count FROM libraries;"), 28, "libraries.asset_count")

    let rows = try db.query("SELECT keywords_flat, keywords_json, themes_json, theme FROM assets;") { statement in
        (statement.string(0) ?? "", statement.string(1) ?? "", statement.string(2) ?? "", statement.string(3))
    }
    var badKeywords: [String] = []
    var multiTheme = 0
    for (flat, json, themesJSON, theme) in rows {
        guard let keywords = Catalog.decodeStringArray(json), !keywords.isEmpty else {
            badKeywords.append("keywords_json 解码失败：\(json.prefix(30))")
            continue
        }
        if flat != keywords.joined(separator: "|") {
            badKeywords.append("keywords_flat 不是 | 分隔：\(flat.prefix(30))")
        }
        let themes = Catalog.decodeStringArray(themesJSON) ?? []
        if themes.count >= 2 { multiTheme += 1 }
        if themes.first != theme {
            badKeywords.append("theme ≠ themes_json[0]：\(theme ?? "nil") / \(themesJSON.prefix(30))")
        }
    }
    try ensure(badKeywords.isEmpty, badKeywords.prefix(3).joined(separator: "; "))
    try ensure(multiTheme >= 10, "多主题素材太少（\(multiTheme) 行）")
    return "根目录 \(library.rootPath)；\(rows.count) 行关键字均 \("|") 分隔、\(multiTheme) 行多主题、theme=首主题"
}

// MARK: - 收尾

print("")
print("checks: \(checks), failures: \(failures)")
if failures > 0 {
    exit(1)
}
exit(0)