import Foundation

/// catalog.db 的只读查询 + 受控写入层。
/// 所有字段按 Windows 版 schema 命名；老库缺少某些列时自动降级为 NULL，不会崩溃。
/// RAW 扩展名集合（用于「RAW+JPG 配对」判定）。
let RAW_EXTENSIONS_SQL = "('cr3','cr2','crw','nef','nrw','arw','srf','sr2','raf','rw2','orf','dng','pef','srw','x3f','rwl','mos','mrw','3fr','erf','mef','iiq','k25','kdc','dcr','raw')"

public final class Catalog: @unchecked Sendable {
    public let directory: URL
    public let databaseURL: URL

    private let columnCacheLock = NSLock()
    private var cachedColumns: Set<String>?
    private var cachedTables: Set<String>?

    public init(directory: URL) throws {
        self.directory = directory.standardizedFileURL
        self.databaseURL = directory.appendingPathComponent("catalog.db")
        guard FileManager.default.fileExists(atPath: databaseURL.path) else {
            throw DatabaseError.notFound("所选目录中没有 catalog.db：\(directory.path)")
        }
        let db = try Database(path: databaseURL.path, readOnly: true)
        _ = try db.scalarInt("SELECT count(*) FROM assets;")
    }

    /// 直接按数据库文件打开（目录标签页的工作区数据库）。
    public init(databaseURL: URL) throws {
        self.directory = databaseURL.deletingLastPathComponent().standardizedFileURL
        self.databaseURL = databaseURL.standardizedFileURL
        guard FileManager.default.fileExists(atPath: databaseURL.path) else {
            throw DatabaseError.notFound("工作区数据库不存在：\(databaseURL.path)")
        }
        let db = try Database(path: databaseURL.path, readOnly: true)
        _ = try db.scalarInt("SELECT count(*) FROM assets;")
    }

    /// 通过目录路径找到 libraries 表里的库 ID（兼容 /tmp 与 /private/tmp 两种写法）。
    public func libraryID(forDirectory directory: URL) throws -> String? {
        let db = try Database(path: databaseURL.path, readOnly: true)
        guard try db.tableExists("libraries") else { return nil }
        let resolved = directory.resolvingSymlinksInPath().standardizedFileURL.path
        let raw = directory.standardizedFileURL.path
        var candidates: Set<String> = [resolved, raw]
        if raw.hasPrefix("/private/") {
            candidates.insert(String(raw.dropFirst("/private".count)))
        } else {
            candidates.insert("/private" + raw)
        }
        let rows: [(String, String)] = try db.query("SELECT id, path FROM libraries;") { st in
            ((try? st.string(0)) ?? "", (try? st.string(1)) ?? "")
        }
        for (id, path) in rows {
            let normalized = URL(fileURLWithPath: path).resolvingSymlinksInPath().standardizedFileURL.path
            if candidates.contains(path) || candidates.contains(normalized) { return id }
        }
        return nil
    }

    // MARK: - 列与表探测

    private var columns: Set<String> {
        get throws {
            columnCacheLock.lock()
            defer { columnCacheLock.unlock() }
            if let cachedColumns { return cachedColumns }
            let db = try Database(path: databaseURL.path, readOnly: true)
            let set = try db.tableColumns("assets")
            cachedColumns = set
            return set
        }
    }

    private var tables: Set<String> {
        get throws {
            columnCacheLock.lock()
            defer { columnCacheLock.unlock() }
            if let cachedTables { return cachedTables }
            let db = try Database(path: databaseURL.path, readOnly: true)
            let names = ["asset_custom", "collections", "collection_assets", "merge_groups",
                         "merge_group_assets", "libraries", "asset_tags", "tags"]
            var set = Set<String>()
            for name in names where try db.tableExists(name) {
                set.insert(name)
            }
            cachedTables = set
            return set
        }
    }

    private func has(_ column: String) throws -> Bool {
        try columns.contains(column)
    }

    // MARK: - 读取素材

    private static let readColumns: [String] = [
        "id", "library_id", "full_path", "file_name", "relative_path", "size_bytes",
        "file_modified_ms", "kind", "format_code", "width", "height", "duration_seconds",
        "captured_at", "captured_ms", "captured_day", "country", "province", "city",
        "latitude", "longitude", "device_key", "camera_make", "camera_model", "lens_model",
        "focal_length_mm", "aperture_f", "iso", "title", "caption",
        "keywords_json", "keywords_flat", "themes_json", "theme", "tone", "rating",
        "color_label", "is_pick", "post_process", "is_log", "log_profile", "xmp_path", "thumb_key", "lut_id",
        "rotation_degrees", "group_id", "is_group_cover", "companions_json", "last_error",
        "ai_subject", "ai_mood", "quality_score",
    ]

    private func selectClause() throws -> String {
        let existing = try columns
        var clauses = Catalog.readColumns.map { column in
            existing.contains(column) ? column : "NULL AS \(column)"
        }
        // 「RAW+JPG 配对」标记：RAW 素材在同目录存在同基名 JPG 时为 1。
        if existing.contains("format_code") && existing.contains("file_name") {
            let raw = "lower(COALESCE(assets.format_code,'')) IN " + RAW_EXTENSIONS_SQL
            let dir = "substr(lower(assets.full_path),1,length(assets.full_path)-length(assets.file_name))"
            let base = "substr(lower(assets.file_name),1,length(assets.file_name)-length(assets.format_code)-1)"
            clauses.append("""
            EXISTS (SELECT 1 FROM assets sibling
                    WHERE sibling.full_path <> assets.full_path
                      AND \(raw)
                      AND lower(COALESCE(sibling.format_code,'')) IN ('jpg','jpeg')
                      AND substr(lower(sibling.full_path),1,length(sibling.full_path)-length(sibling.file_name))
                          = \(dir)
                      AND substr(lower(sibling.file_name),1,length(sibling.file_name)-length(sibling.format_code)-1)
                          = \(base)) AS has_jpeg_sibling
            """)
        }
        return clauses.joined(separator: ", ")
    }

    private func makeAsset(_ statement: Statement, index: [String: Int32]) -> Asset? {
        func text(_ name: String) -> String? {
            guard let position = index[name] else { return nil }
            return statement.string(position)
        }
        func number(_ name: String) -> Int? {
            guard let position = index[name] else { return nil }
            return statement.int(position)
        }
        func doubleValue(_ name: String) -> Double? {
            guard let position = index[name] else { return nil }
            return statement.double(position)
        }
        guard let id = text("id"), let path = text("full_path") else { return nil }
        let hasJpegSibling: Bool = {
            guard let position = index["has_jpeg_sibling"] else { return false }
            return statement.int(position) == 1
        }()
        let keywords: [String]
        if let raw = text("keywords_json"), let list = Catalog.decodeStringArray(raw) {
            keywords = list
        } else if let flat = text("keywords_flat") {
            keywords = flat.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        } else {
            keywords = []
        }
        var themes = text("themes_json").flatMap(Catalog.decodeStringArray) ?? []
        if themes.isEmpty, let single = text("theme"), !single.isEmpty {
            themes = [single]
        }
        let capturedAt: Date?
        if let ms = doubleValue("captured_ms") ?? number("captured_ms").map(Double.init) {
            capturedAt = Date(timeIntervalSince1970: ms / 1000)
        } else if let raw = text("captured_at") {
            capturedAt = Catalog.parseISO8601(raw)
        } else {
            capturedAt = nil
        }
        let modifiedAt: Date? = doubleValue("file_modified_ms").map { Date(timeIntervalSince1970: $0 / 1000) }
        return Asset(
            id: id,
            libraryID: text("library_id") ?? "default",
            path: path,
            fileName: text("file_name") ?? URL(fileURLWithPath: path).lastPathComponent,
            relativePath: text("relative_path"),
            kind: number("kind") ?? 1,
            formatCode: text("format_code") ?? "jpeg",
            sizeBytes: number("size_bytes").map(Int64.init) ?? 0,
            fileModifiedAt: modifiedAt,
            width: number("width") ?? 0,
            height: number("height") ?? 0,
            durationSeconds: doubleValue("duration_seconds") ?? 0,
            capturedAt: capturedAt,
            capturedDay: text("captured_day"),
            country: text("country"),
            province: text("province"),
            city: text("city"),
            latitude: doubleValue("latitude"),
            longitude: doubleValue("longitude"),
            deviceKey: text("device_key"),
            cameraMake: text("camera_make"),
            cameraModel: text("camera_model"),
            lensModel: text("lens_model"),
            focalLength: doubleValue("focal_length_mm"),
            aperture: doubleValue("aperture_f"),
            iso: number("iso"),
            title: text("title"),
            caption: text("caption"),
            keywords: keywords,
            themes: themes,
            tone: text("tone"),
            rating: number("rating") ?? 0,
            colorLabel: text("color_label"),
            isPick: number("is_pick"),
            postProcess: number("post_process") ?? 1,
            isLog: number("is_log").map { $0 != 0 } ?? false,
            logProfile: text("log_profile"),
            xmpPath: text("xmp_path"),
            thumbKey: text("thumb_key"),
            lut: text("lut_id"),
            rotationDegrees: number("rotation_degrees") ?? 0,
            groupID: text("group_id"),
            isGroupCover: number("is_group_cover").map { $0 != 0 } ?? false,
            companions: text("companions_json").flatMap(Catalog.decodeStringArray) ?? [],
            lastError: text("last_error"),
            aiSubject: text("ai_subject"),
            aiMood: text("ai_mood"),
            qualityScore: doubleValue("quality_score"),
            hasJpegSibling: hasJpegSibling
        )
    }

    private func readAssets(sql: String, binds: [Any?]) throws -> [Asset] {
        let db = try Database(path: databaseURL.path, readOnly: true)
        let statement = try db.prepare(sql)
        try statement.bindAll(binds)
        var index: [String: Int32] = [:]
        for i in 0..<statement.columnCount() {
            // 优先用 SQL 实际列名（含 has_jpeg_sibling 这类计算列），回退到声明的列顺序。
            index[statement.name(at: i) ?? Catalog.readColumns[Int(i)]] = i
        }
        var assets: [Asset] = []
        while try statement.step() {
            if let asset = makeAsset(statement, index: index) {
                assets.append(asset)
            }
        }
        return assets
    }

    // MARK: - 筛选 SQL

    private func escapeLike(_ value: String) -> String {
        value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "%", with: "\\%")
            .replacingOccurrences(of: "_", with: "\\_")
    }

    private func clause(for dimension: FilterDimension, values: [String], unset: Bool) throws -> (String, [Any]) {
        guard !values.isEmpty || unset else { return ("", []) }
        let placeholders = Array(repeating: "?", count: values.count).joined(separator: ", ")
        switch dimension {
        case .kind:
            var pieces: [String] = []
            var binds: [Any] = []
            if !values.isEmpty {
                pieces.append("kind IN (\(placeholders))")
                binds.append(contentsOf: values.compactMap { Int($0) }.map { $0 as Any })
            }
            return (pieces.joined(separator: " OR "), binds)
        case .format:
            return ("format_code IN (\(placeholders))", values)
        case .country:
            return ("country IN (\(placeholders))", values)
        case .city:
            let nullPart = "(city IS NULL OR city = '')"
            if unset {
                return ("(\(nullPart) OR city IN (\(placeholders)))", values)
            }
            return ("city IN (\(placeholders))", values)
        case .device:
            if unset {
                return ("(device_key IS NULL OR device_key = '' OR device_key IN (\(placeholders)))", values)
            }
            return ("device_key IN (\(placeholders))", values)
        case .theme:
            var pieces: [String] = []
            var binds: [Any] = []
            for value in values {
                pieces.append("theme = ?")
                binds.append(value)
                pieces.append("themes_json LIKE ? ESCAPE '\\'")
                binds.append("%\"\(escapeLike(value))\"%")
            }
            if unset {
                pieces.append("(theme IS NULL OR theme = '')")
            }
            return (pieces.joined(separator: " OR "), binds)
        case .tone:
            if unset {
                return ("(tone IS NULL OR tone = '' OR tone IN (\(placeholders)))", values)
            }
            return ("tone IN (\(placeholders))", values)
        case .rating:
            if unset {
                return ("(rating IS NULL OR rating = 0 OR rating IN (\(placeholders)))",
                        values.compactMap { Int($0) })
            }
            return ("rating IN (\(placeholders))", values.compactMap { Int($0) })
        case .colorLabel:
            if unset {
                return ("(color_label IS NULL OR color_label = '' OR color_label IN (\(placeholders)))", values)
            }
            return ("color_label IN (\(placeholders))", values)
        case .collection:
            return ("id IN (SELECT asset_id FROM collection_assets WHERE collection_id IN (\(placeholders)))", values)
        case .year:
            var pieces: [String] = []
            var binds: [Any] = []
            for value in values {
                pieces.append("substr(captured_day, 1, 4) = ?")
                binds.append(value)
            }
            if unset {
                pieces.append("(captured_day IS NULL OR captured_day = '')")
            }
            return (pieces.joined(separator: " OR "), binds)
        }
    }

    private func whereClause(_ filter: AssetFilter, coverOnly: Bool, expandedGroupID: String?) throws -> (String, [Any]) {
        var pieces: [String] = []
        var binds: [Any] = []

        if coverOnly, try has("group_id") {
            pieces.append("(is_group_cover = 1 OR group_id IS NULL OR group_id = '')")
        }
        if !filter.libraryIDs.isEmpty, try has("library_id") {
            pieces.append("assets.library_id IN (\(Array(repeating: "?", count: filter.libraryIDs.count).joined(separator: ", ")))")
            binds.append(contentsOf: filter.libraryIDs.sorted())
        }
        if let expandedGroupID, try has("group_id") {
            pieces.append("group_id = ?")
            binds.append(expandedGroupID)
        }

        // RAW+JPG 折叠：同名（同目录、去掉扩展名）存在 RAW 时隐藏 JPG 伴生行，
        // 只保留 RAW 作为封面。仅当库里有 format_code 列时启用。
        if filter.foldRawPairs, try has("format_code") {
            let rawFormats = ["cr3", "cr2", "crw", "nef", "nrw", "arw", "srf", "sr2", "raf",
                              "rw2", "orf", "dng", "pef", "srw", "x3f", "rwl", "mos", "mrw",
                              "3fr", "erf", "mef", "iiq", "k25", "kdc", "dcr", "raw"]
            let rawList = rawFormats.map { "'\($0)'" }.joined(separator: ", ")
            pieces.append("""
            NOT (lower(COALESCE(assets.format_code, '')) IN ('jpg', 'jpeg') AND EXISTS (
                SELECT 1 FROM assets raw
                WHERE lower(COALESCE(raw.format_code, '')) IN (\(rawList))
                  AND substr(lower(raw.full_path), 1, length(raw.full_path) - length(raw.file_name))
                      = substr(lower(assets.full_path), 1, length(assets.full_path) - length(assets.file_name))
                  AND substr(lower(raw.file_name), 1, length(raw.file_name) - length(raw.format_code) - 1)
                      = substr(lower(assets.file_name), 1, length(assets.file_name) - length(assets.format_code) - 1)
            ))
            """)
        }

        let trimmed = filter.searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty {
            let tokens = trimmed.split(whereSeparator: { $0.isWhitespace }).map(String.init)
            for token in tokens {
                let pattern = "%\(escapeLike(token))%"
                var fields = ["file_name LIKE ? ESCAPE '\\'", "full_path LIKE ? ESCAPE '\\'",
                              "keywords_flat LIKE ? ESCAPE '\\'", "title LIKE ? ESCAPE '\\'",
                              "caption LIKE ? ESCAPE '\\'"]
                var tokenBinds: [Any] = [pattern, pattern, pattern, pattern, pattern]
                if try has("city") {
                    fields.append("city LIKE ? ESCAPE '\\'")
                    tokenBinds.append(pattern)
                }
                if try has("themes_json") {
                    fields.append("themes_json LIKE ? ESCAPE '\\'")
                    tokenBinds.append(pattern)
                }
                pieces.append("(" + fields.joined(separator: " OR ") + ")")
                binds.append(contentsOf: tokenBinds)
            }
        }

        func add(_ dimension: FilterDimension, _ values: [String]) throws {
            let (sql, values) = try clause(for: dimension, values: values, unset: filter.unsetDimensions.contains(dimension))
            if !sql.isEmpty {
                pieces.append("(\(sql))")
                binds.append(contentsOf: values.map { $0 as Any })
            }
        }

        try add(.kind, filter.kinds.map(String.init))
        try add(.format, Array(filter.formats))
        try add(.city, Array(filter.cities))
        try add(.device, Array(filter.devices))
        try add(.theme, Array(filter.themes))
        try add(.tone, Array(filter.tones))
        try add(.rating, filter.ratings.map(String.init))
        try add(.colorLabel, Array(filter.colorLabels))
        try add(.year, Array(filter.years))
        try add(.collection, Array(filter.collectionIDs))

        if let pick = filter.pick, try has("is_pick") {
            pieces.append("is_pick = ?")
            binds.append(pick)
        }
        if filter.logMode != nil, try has("is_log") {
            pieces.append("is_log = ?")
            binds.append(filter.logMode! ? 1 : 0)
        }
        if !filter.postProcesses.isEmpty, try has("post_process") {
            pieces.append("post_process IN (\(Array(repeating: "?", count: filter.postProcesses.count).joined(separator: ", ")))")
            binds.append(contentsOf: filter.postProcesses.map { $0 as Any })
        }
        if !filter.mergeGroupIDs.isEmpty, try has("group_id") {
            pieces.append("group_id IN (\(Array(repeating: "?", count: filter.mergeGroupIDs.count).joined(separator: ", ")))")
            binds.append(contentsOf: filter.mergeGroupIDs.map { $0 as Any })
        }
        if filter.unsetDimensions.contains(.collection), try tables.contains("collection_assets") {
            pieces.append("id NOT IN (SELECT asset_id FROM collection_assets)")
        }
        return (pieces.isEmpty ? "" : " WHERE " + pieces.joined(separator: " AND "), binds)
    }

    private func orderClause(_ sort: SortDescriptor) -> String {
        let direction = sort.ascending ? "ASC" : "DESC"
        switch sort.key {
        case .captured:
            return "ORDER BY captured_ms IS NULL, captured_ms \(direction), captured_day \(direction), file_name ASC"
        case .fileName:
            return "ORDER BY file_name COLLATE NOCASE \(direction)"
        case .modified:
            return "ORDER BY file_modified_ms IS NULL, file_modified_ms \(direction), file_name ASC"
        case .size:
            return "ORDER BY size_bytes \(direction), file_name ASC"
        case .rating:
            return "ORDER BY rating \(direction), captured_ms IS NULL, captured_ms \(direction)"
        case .indexed:
            return "ORDER BY indexed_ms IS NULL, indexed_ms \(direction), file_name ASC"
        }
    }

    // MARK: - 公开读取 API

    public func loadAssets(filter: AssetFilter = AssetFilter(),
                           sort: SortDescriptor = SortDescriptor(),
                           limit: Int = 20_000,
                           offset: Int = 0,
                           expandedGroupID: String? = nil,
                           includeGroupMembers: Bool = false) throws -> [Asset] {
        let coverOnly = !includeGroupMembers && expandedGroupID == nil
        let (whereSql, binds) = try whereClause(filter, coverOnly: coverOnly, expandedGroupID: expandedGroupID)
        let sql = """
        SELECT \(try selectClause())
        FROM assets\(whereSql)
        \(orderClause(sort))
        LIMIT ? OFFSET ?
        """
        var allBinds = binds
        allBinds.append(limit)
        allBinds.append(offset)
        return try readAssets(sql: sql, binds: allBinds)
    }

    public func count(filter: AssetFilter = AssetFilter(), expandedGroupID: String? = nil) throws -> Int {
        let (whereSql, binds) = try whereClause(filter, coverOnly: expandedGroupID == nil, expandedGroupID: expandedGroupID)
        let db = try Database(path: databaseURL.path, readOnly: true)
        return try db.scalarInt("SELECT count(*) FROM assets\(whereSql);", binds: binds)
    }

    public func loadFacets(filter: AssetFilter = AssetFilter(), expandedGroupID: String? = nil) throws -> Facets {
        var facets = Facets()
        let db = try Database(path: databaseURL.path, readOnly: true)
        let hasGroup = try has("group_id")
        let coverOnly = expandedGroupID == nil
        let (whereSql, facetBinds) = try whereClause(filter, coverOnly: coverOnly, expandedGroupID: expandedGroupID)
        // 老库缺列时降级为 NULL，保持列顺序不变。
        let existing = try columns
        let needed = ["kind", "format_code", "city", "country", "device_key", "captured_day",
                      "themes_json", "theme", "tone", "rating", "color_label", "is_pick",
                      "post_process", "is_log"]
        let projection = needed.map { existing.contains($0) ? $0 : "NULL AS \($0)" }
            .joined(separator: ", ")
        let sql = """
        SELECT \(projection)
        FROM assets\(whereSql);
        """
        var kindCounts: [String: Int] = [:]
        var formatCounts: [String: Int] = [:]
        var cityCounts: [String: Int] = [:]
        var countryCounts: [String: Int] = [:]
        var deviceCounts: [String: Int] = [:]
        var yearCounts: [String: Int] = [:]
        var themeCounts: [String: Int] = [:]
        var toneCounts: [String: Int] = [:]
        var ratingCounts: [String: Int] = [:]
        var labelCounts: [String: Int] = [:]
        var unset: [FilterDimension: Int] = [:]

        let statement = try db.prepare(sql)
        try statement.bindAll(facetBinds.map { $0 as Any? })
        while try statement.step() {
            facets.total += 1
            if let kind = statement.int(0) {
                kindCounts[String(kind), default: 0] += 1
            }
            if let format = statement.string(1), !format.isEmpty {
                formatCounts[format, default: 0] += 1
            }
            if let city = statement.string(2), !city.isEmpty {
                cityCounts[city, default: 0] += 1
            } else {
                unset[.city, default: 0] += 1
            }
            if let country = statement.string(3), !country.isEmpty {
                countryCounts[country, default: 0] += 1
            } else {
                unset[.country, default: 0] += 1
            }
            if let device = statement.string(4), !device.isEmpty {
                deviceCounts[device, default: 0] += 1
            } else {
                unset[.device, default: 0] += 1
            }
            if let day = statement.string(5), day.count >= 4 {
                yearCounts[String(day.prefix(4)), default: 0] += 1
            } else {
                unset[.year, default: 0] += 1
            }
            var themes = statement.string(6).flatMap(Catalog.decodeStringArray) ?? []
            if themes.isEmpty, let single = statement.string(7), !single.isEmpty {
                themes = [single]
            }
            if themes.isEmpty {
                unset[.theme, default: 0] += 1
            } else {
                for theme in themes {
                    themeCounts[theme, default: 0] += 1
                }
            }
            if let tone = statement.string(8), !tone.isEmpty {
                toneCounts[tone, default: 0] += 1
            } else {
                unset[.tone, default: 0] += 1
            }
            let rating = statement.int(9) ?? 0
            ratingCounts[String(rating), default: 0] += 1
            if let label = statement.string(10), !label.isEmpty {
                labelCounts[label, default: 0] += 1
            } else {
                unset[.colorLabel, default: 0] += 1
            }
            switch statement.int(11) {
            case 1?: facets.picked += 1
            case 0?: facets.rejected += 1
            default: facets.unpicked += 1
            }
            switch statement.int(12) ?? 1 {
            case 2: facets.processed += 1
            default: facets.unprocessed += 1
            }
            if (statement.int(13) ?? 0) != 0 {
                facets.logCount += 1
            }
        }
        facets.kinds = kindCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { $0.value > $1.value }
        facets.formats = formatCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { $0.value > $1.value }
        facets.years = yearCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { $0.value > $1.value }
        facets.cities = cityCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { $0.value > $1.value }
        facets.countries = countryCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { $0.value > $1.value }
        facets.devices = deviceCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { $0.value > $1.value }
        facets.themes = themeCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { $0.value > $1.value }
        facets.tones = toneCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { $0.value > $1.value }
        facets.ratings = ratingCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { Int($0.value)! < Int($1.value)! }
        facets.colorLabels = labelCounts.map { FacetItem(value: $0.key, count: $0.value) }.sorted { $0.value > $1.value }
        facets.unsetCounts = unset
        return facets
    }

    // MARK: - 合集 / 合并组 / 库

    public func collections() throws -> [AssetCollection] {
        guard try tables.contains("collections") else { return [] }
        let db = try Database(path: databaseURL.path, readOnly: true)
        let hasLinks = try tables.contains("collection_assets")
        let countExpr = hasLinks ? "(SELECT count(*) FROM collection_assets ca WHERE ca.collection_id = c.id)" : "0"
        return try db.query("""
        SELECT c.id, c.name, c.description, c.sort_order, \(countExpr)
        FROM collections c ORDER BY c.sort_order, c.name;
        """) { statement in
            AssetCollection(id: statement.string(0) ?? "",
                            name: statement.string(1) ?? "",
                            detail: statement.string(2),
                            count: statement.int(4) ?? 0,
                            sortOrder: statement.int(3) ?? 0)
        }
    }

    /// 合集成员工件 ID 列表（按合集 id 分组），供设置交换导出使用。
    public func collectionMembers() throws -> [String: [String]] {
        guard try tables.contains("collections"), try tables.contains("collection_assets") else { return [:] }
        let db = try Database(path: databaseURL.path, readOnly: true)
        var members: [String: [String]] = [:]
        try db.query("SELECT collection_id, asset_id FROM collection_assets ORDER BY collection_id, added_at;") { statement in
            guard let collectionID = statement.string(0), let assetID = statement.string(1) else { return }
            members[collectionID, default: []].append(assetID)
        }
        return members
    }

    public func mergeGroups() throws -> [MergeGroup] {
        guard try tables.contains("merge_groups") else { return [] }
        let db = try Database(path: databaseURL.path, readOnly: true)
        let hasLinks = try tables.contains("merge_group_assets")
        let countExpr = hasLinks ? "(SELECT count(*) FROM merge_group_assets m WHERE m.group_id = g.id)" : "0"
        return try db.query("""
        SELECT g.id, coalesce(g.name, ''), g.cover_asset_id, \(countExpr),
               (SELECT file_name FROM assets a WHERE a.id = g.cover_asset_id)
        FROM merge_groups g ORDER BY g.sort_order, g.name;
        """) { statement in
            MergeGroup(id: statement.string(0) ?? "",
                       name: statement.string(1) ?? "",
                       coverAssetID: statement.string(2),
                       count: statement.int(3) ?? 0,
                       coverFileName: statement.string(4))
        }
    }

    public func libraries() throws -> [LibraryRoot] {
        guard try tables.contains("libraries") else { return [] }
        let db = try Database(path: databaseURL.path, readOnly: true)
        let hasCount = try db.tableColumns("libraries").contains("asset_count")
        let sql = hasCount
            ? "SELECT id, name, root_path, asset_count FROM libraries ORDER BY name;"
            : "SELECT id, name, root_path, 0 AS asset_count FROM libraries ORDER BY name;"
        return try db.query(sql) { statement in
            LibraryRoot(id: statement.string(0) ?? "",
                        name: statement.string(1) ?? "",
                        rootPath: statement.string(2) ?? "",
                        count: statement.int(3) ?? 0)
        }
    }

    public func groupMembers(groupID: String) throws -> [Asset] {
        guard try has("group_id") else { return [] }
        let sql = """
        SELECT \(try selectClause()) FROM assets WHERE group_id = ?
        ORDER BY (SELECT position FROM merge_group_assets m WHERE m.asset_id = assets.id), file_name;
        """
        return try readAssets(sql: sql, binds: [groupID])
    }

    public func members(ofGroupIDs ids: [String]) throws -> [String] {
        guard try tables.contains("merge_group_assets"), !ids.isEmpty else { return [] }
        let db = try Database(path: databaseURL.path, readOnly: true)
        let placeholders = Array(repeating: "?", count: ids.count).joined(separator: ", ")
        return try db.query("SELECT asset_id FROM merge_group_assets WHERE group_id IN (\(placeholders));",
                            binds: ids) { $0.string(0) ?? "" }
    }

    // MARK: - 写入

    private func writable(_ column: String, in db: Database) throws -> Bool {
        try db.tableColumns("assets").contains(column)
    }

    public func update(patch: AssetPatch, ids: [String]) throws {
        guard !ids.isEmpty, !patch.isEmpty else { return }
        let db = try Database(path: databaseURL.path, readOnly: false)
        var setPieces: [String] = []
        var binds: [Any] = []

        func add(_ column: String, _ value: Any?) throws {
            guard try writable(column, in: db) else { return }
            setPieces.append("\(column) = ?")
            binds.append(value)
        }

        if let rating = patch.rating { try add("rating", rating) }
        if let post = patch.postProcess { try add("post_process", post) }
        switch patch.city {
        case .unchanged: break
        case .clear: try add("city", nil)
        case .set(let value): try add("city", value)
        }
        switch patch.country {
        case .unchanged: break
        case .clear: try add("country", nil)
        case .set(let value): try add("country", value)
        }
        switch patch.province {
        case .unchanged: break
        case .clear: try add("province", nil)
        case .set(let value): try add("province", value)
        }
        switch patch.colorLabel {
        case .unchanged: break
        case .clear: try add("color_label", nil)
        case .set(let value): try add("color_label", value.isEmpty ? nil : value)
        }
        switch patch.isPick {
        case .unchanged: break
        case .clear: try add("is_pick", nil)
        case .set(let value): try add("is_pick", value)
        }
        switch patch.tone {
        case .unchanged: break
        case .clear: try add("tone", nil)
        case .set(let value): try add("tone", value.isEmpty ? nil : value)
        }
        switch patch.isLog {
        case .unchanged: break
        case .clear: try add("is_log", 0)
        case .set(let value): try add("is_log", value ? 1 : 0)
        }
        switch patch.aiSubject {
        case .unchanged: break
        case .clear: try add("ai_subject", nil)
        case .set(let value): try add("ai_subject", value.isEmpty ? nil : value)
        }
        switch patch.aiMood {
        case .unchanged: break
        case .clear: try add("ai_mood", nil)
        case .set(let value): try add("ai_mood", value.isEmpty ? nil : value)
        }
        switch patch.keywords {
        case .unchanged: break
        case .clear:
            try add("keywords_json", "[]")
            try add("keywords_flat", "")
        case .set(let values):
            let cleaned = values.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            if let data = try? JSONSerialization.data(withJSONObject: cleaned, options: []),
               let json = String(data: data, encoding: .utf8) {
                try add("keywords_json", json)
            } else {
                try add("keywords_json", "[]")
            }
            try add("keywords_flat", cleaned.joined(separator: ","))
        }
        if let quality = patch.qualityScore { try add("quality_score", quality) }
        switch patch.themes {
        case .unchanged: break
        case .clear:
            // Windows DDL 里 themes_json 是 NOT NULL DEFAULT '[]'，清空须写空数组而不是 NULL。
            if try has("themes_json") {
                try add("themes_json", "[]")
            }
            try add("theme", nil)
        case .set(let values):
            let cleaned = values.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            if let data = try? JSONSerialization.data(withJSONObject: cleaned, options: []),
               let json = String(data: data, encoding: .utf8) {
                try add("themes_json", json)
            } else {
                try add("themes_json", "[]")
            }
            try add("theme", cleaned.first)
        }

        guard !setPieces.isEmpty else { return }
        let placeholders = Array(repeating: "?", count: ids.count).joined(separator: ", ")
        let sql = "UPDATE assets SET \(setPieces.joined(separator: ", ")) WHERE id IN (\(placeholders));"
        try db.transaction {
            let statement = try db.prepare(sql)
            try statement.bindAll(binds + ids)
            _ = try statement.step()
        }
    }

    /// 整理（移动）后回写文件位置：full_path / file_name / relative_path。
    public func updateLocations(_ locations: [String: URL], relativeTo root: URL? = nil) throws {
        guard !locations.isEmpty else { return }
        let db = try Database(path: databaseURL.path, readOnly: false)
        let hasRelative = try db.tableColumns("assets").contains("relative_path")
        let sql = hasRelative
            ? "UPDATE assets SET full_path = ?, file_name = ?, relative_path = ? WHERE id = ?;"
            : "UPDATE assets SET full_path = ?, file_name = ? WHERE id = ?;"
        try db.transaction {
            let statement = try db.prepare(sql)
            for (id, url) in locations {
                statement.reset()
                var binds: [Any?] = [url.path, url.lastPathComponent]
                if hasRelative {
                    binds.append(Self.relativePath(of: url, relativeTo: root))
                }
                binds.append(id)
                try statement.bindAll(binds)
                _ = try statement.step()
            }
        }
    }

    static func relativePath(of url: URL, relativeTo root: URL?) -> String? {
        guard let root else { return nil }
        let base = root.standardizedFileURL.path
        let full = url.standardizedFileURL.path
        guard full.hasPrefix(base) else { return nil }
        var relative = String(full.dropFirst(base.count))
        while relative.hasPrefix("/") { relative.removeFirst() }
        return relative.isEmpty ? nil : relative
    }

    public func delete(ids: [String]) throws {
        guard !ids.isEmpty else { return }
        let db = try Database(path: databaseURL.path, readOnly: false)
        let placeholders = Array(repeating: "?", count: ids.count).joined(separator: ", ")
        try db.transaction {
            if try db.tableExists("collection_assets") {
                try db.execute("DELETE FROM collection_assets WHERE asset_id IN (\(placeholders));")
            }
            if try db.tableExists("merge_group_assets") {
                try db.execute("DELETE FROM merge_group_assets WHERE asset_id IN (\(placeholders));")
            }
            let statement = try db.prepare("DELETE FROM assets WHERE id IN (\(placeholders));")
            try statement.bindAll(ids)
            _ = try statement.step()
        }
    }

    public func ensureWriteTables() throws {
        let db = try Database(path: databaseURL.path, readOnly: false)
        try db.execute("""
        CREATE TABLE IF NOT EXISTS collections (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT,
            created_at TEXT, created_ms INTEGER, sort_order INTEGER NOT NULL DEFAULT 0);
        """)
        try db.execute("""
        CREATE TABLE IF NOT EXISTS collection_assets (
            collection_id TEXT NOT NULL, asset_id TEXT NOT NULL, added_at TEXT, added_ms INTEGER,
            PRIMARY KEY (collection_id, asset_id));
        """)
        try db.execute("""
        CREATE TABLE IF NOT EXISTS merge_groups (
            id TEXT PRIMARY KEY, name TEXT, cover_asset_id TEXT, created_at TEXT, created_ms INTEGER,
            sort_order INTEGER NOT NULL DEFAULT 0);
        """)
        try db.execute("""
        CREATE TABLE IF NOT EXISTS merge_group_assets (
            group_id TEXT NOT NULL, asset_id TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0,
            added_at TEXT, PRIMARY KEY (group_id, asset_id));
        """)
        cachedTables = nil
    }

    @discardableResult
    public func createCollection(name: String) throws -> String {
        try ensureWriteTables()
        let db = try Database(path: databaseURL.path, readOnly: false)
        let id = UUID().uuidString
        let statement = try db.prepare("INSERT INTO collections (id, name, created_ms, sort_order) VALUES (?, ?, ?, ?);")
        let order = try db.scalarInt("SELECT coalesce(max(sort_order), 0) + 1 FROM collections;")
        try statement.bindAll([id, name, Int(Date().timeIntervalSince1970 * 1000), order])
        _ = try statement.step()
        return id
    }

    public func renameCollection(id: String, name: String) throws {
        try ensureWriteTables()
        let db = try Database(path: databaseURL.path, readOnly: false)
        let statement = try db.prepare("UPDATE collections SET name = ? WHERE id = ?;")
        try statement.bindAll([name, id])
        _ = try statement.step()
    }

    public func deleteCollection(id: String) throws {
        try ensureWriteTables()
        let db = try Database(path: databaseURL.path, readOnly: false)
        try db.transaction {
            try db.execute("DELETE FROM collection_assets WHERE collection_id = ?;")
            let statement = try db.prepare("DELETE FROM collections WHERE id = ?;")
            try statement.bindAll([id])
            _ = try statement.step()
        }
    }

    public func addToCollection(collectionID: String, assetIDs: [String]) throws {
        guard !assetIDs.isEmpty else { return }
        try ensureWriteTables()
        let db = try Database(path: databaseURL.path, readOnly: false)
        try db.transaction {
            let statement = try db.prepare("INSERT OR IGNORE INTO collection_assets (collection_id, asset_id, added_ms) VALUES (?, ?, ?);")
            let now = Int(Date().timeIntervalSince1970 * 1000)
            for assetID in assetIDs {
                statement.reset()
                try statement.bindAll([collectionID, assetID, now])
                _ = try statement.step()
            }
        }
    }

    public func removeFromCollection(collectionID: String, assetIDs: [String]) throws {
        guard !assetIDs.isEmpty else { return }
        try ensureWriteTables()
        let db = try Database(path: databaseURL.path, readOnly: false)
        let placeholders = Array(repeating: "?", count: assetIDs.count).joined(separator: ", ")
        let statement = try db.prepare("DELETE FROM collection_assets WHERE collection_id = ? AND asset_id IN (\(placeholders));")
        try statement.bindAll([collectionID] + assetIDs)
        _ = try statement.step()
    }

    /// 把合并组封面展开成组内全部成员，用于批量编辑/导出。
    public func expandedIDs(for ids: [String]) throws -> [String] {
        guard try has("group_id") else { return ids }
        let db = try Database(path: databaseURL.path, readOnly: true)
        let placeholders = Array(repeating: "?", count: ids.count).joined(separator: ", ")
        let groups = try db.query("SELECT DISTINCT group_id FROM assets WHERE id IN (\(placeholders)) AND group_id IS NOT NULL AND group_id <> '';",
                                  binds: ids) { $0.string(0) ?? "" }
        guard !groups.isEmpty else { return ids }
        let groupPlaceholders = Array(repeating: "?", count: groups.count).joined(separator: ", ")
        let members = try db.query("SELECT id FROM assets WHERE group_id IN (\(groupPlaceholders));",
                                   binds: groups) { $0.string(0) ?? "" }
        var seen = Set(ids)
        var result = ids
        for member in members where !seen.contains(member) {
            seen.insert(member)
            result.append(member)
        }
        return result
    }

    // MARK: - 工具

    public static func decodeStringArray(_ raw: String) -> [String]? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.hasPrefix("[") else { return nil }
        guard let data = trimmed.data(using: .utf8),
              let value = try? JSONSerialization.jsonObject(with: data) else { return nil }
        if let list = value as? [String] { return list }
        if let list = value as? [Any] {
            return list.compactMap { $0 as? String }
        }
        return nil
    }

    private static let isoFormatters: [ISO8601DateFormatter] = {
        let withFraction = ISO8601DateFormatter()
        withFraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        return [withFraction, plain]
    }()

    public static func parseISO8601(_ raw: String) -> Date? {
        for formatter in isoFormatters {
            if let date = formatter.date(from: raw) { return date }
        }
        let simple = DateFormatter()
        simple.locale = Locale(identifier: "en_US_POSIX")
        simple.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return simple.date(from: raw)
    }
}
