// Copyright (c) Stjarna Perspective. MIT.
//
// 只读 Catalog：直接读 Windows 版建立的 catalog.db（SQLite WAL），
// 以及同一份缩略图缓存。macOS 端目前只读，不写库。

import Foundation
import SQLite3

let SQLITE_TRANSIENT_UNSAFE = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

// MARK: - 模型

struct Asset: Identifiable, Hashable {
    let id: String
    let path: String
    let fileName: String
    let kind: Int              // 1 图片 / 2 视频 / 3 音频
    let width: Int
    let height: Int
    let durationSeconds: Double
    let capturedAt: Date?
    let capturedDay: String?
    let country: String?
    let province: String?
    let city: String?
    let deviceKey: String?
    let cameraModel: String?
    let lensModel: String?
    let theme: String?
    let tone: String?
    let rating: Int
    let colorLabel: String?
    let isPick: Bool
    let isLog: Bool
    let logProfile: String?
    let keywords: [String]
    let thumbKey: String?
    let lutId: String?
    let isGroupCover: Bool

    var isImage: Bool { kind == 1 }
    var isVideo: Bool { kind == 2 }
    var isAudio: Bool { kind == 3 }

    var kindText: String {
        switch kind {
        case 1: return "图片"
        case 2: return "视频"
        case 3: return "音频"
        default: return "未知"
        }
    }

    var dimensionText: String {
        width > 0 && height > 0 ? "\(width)×\(height)" : "—"
    }

    var durationText: String? {
        guard isVideo, durationSeconds > 0 else { return nil }
        let total = Int(durationSeconds.rounded())
        return String(format: "%d:%02d", total / 60, total % 60)
    }

    var capturedText: String {
        guard let capturedDay else { return "—" }
        guard let capturedAt else { return capturedDay }
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH:mm"
        return formatter.string(from: capturedAt)
    }

    var locationText: String? {
        let parts = [country, province, city].compactMap { $0 }.filter { !$0.isEmpty }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    var fileURL: URL { URL(fileURLWithPath: path) }
}

// MARK: - 筛选

struct LibraryFilter: Equatable {
    var searchText: String = ""
    var kinds: Set<Int> = []
    var year: String? = nil
    var city: String? = nil
    var device: String? = nil
    var theme: String? = nil
    var minRating: Int = 0
    var onlyPick: Bool = false
    var onlyLog: Bool = false
    var onlyCover: Bool = true

    var isActive: Bool {
        !searchText.isEmpty || !kinds.isEmpty || year != nil || city != nil ||
        device != nil || theme != nil || minRating > 0 || onlyPick || onlyLog || !onlyCover
    }
}

enum SortKey: String, CaseIterable, Identifiable {
    case capturedDesc, capturedAsc, ratingDesc, nameAsc, sizeDesc

    var id: String { rawValue }
    var title: String {
        switch self {
        case .capturedDesc: return "拍摄时间（新→旧）"
        case .capturedAsc: return "拍摄时间（旧→新）"
        case .ratingDesc: return "评分高→低"
        case .nameAsc: return "文件名 A→Z"
        case .sizeDesc: return "文件大→小"
        }
    }
}

// MARK: - Facet 计数

struct FacetCount: Identifiable, Hashable {
    let value: String
    let count: Int
    var id: String { value }
}

struct KindFacet: Identifiable, Hashable {
    let kind: Int
    let count: Int
    var id: Int { kind }
}

struct Facets {
    var kinds: [KindFacet] = []
    var years: [FacetCount] = []
    var cities: [FacetCount] = []
    var devices: [FacetCount] = []
    var themes: [FacetCount] = []
}

// MARK: - Catalog

final class Catalog {
    let dbPath: String
    private(set) var totalCount: Int = 0

    init(dbPath: String) {
        self.dbPath = dbPath
    }

    /// 数据目录里必须有 catalog.db；缩略图缓存在 <dir>/Cache/thumbs。
    static func validate(directory: URL) -> URL? {
        let db = directory.appendingPathComponent("catalog.db")
        return FileManager.default.fileExists(atPath: db.path) ? db : nil
    }

    static func defaultCandidates() -> [URL] {
        var list: [URL] = []
        let fm = FileManager.default
        if let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first {
            list.append(support.appendingPathComponent("StjMediaViewer"))
        }
        list.append(fm.homeDirectoryForCurrentUser.appendingPathComponent("StjMediaViewer"))
        list.append(fm.homeDirectoryForCurrentUser.appendingPathComponent("Documents/StjMediaViewer"))
        if let saved = UserDefaults.standard.string(forKey: "dataDirectory"), !saved.isEmpty {
            list.insert(URL(fileURLWithPath: saved), at: 0)
        }
        return list
    }

    var thumbnailDirectory: URL {
        URL(fileURLWithPath: dbPath).deletingLastPathComponent()
            .appendingPathComponent("Cache/thumbs")
    }

    // MARK: 查询

    func loadAssets(filter: LibraryFilter, sort: SortKey, limit: Int = 4000) throws -> [Asset] {
        var conditions: [String] = []
        var bindings: [String] = []

        if filter.onlyCover { conditions.append("COALESCE(is_group_cover, 1) = 1") }
        if !filter.kinds.isEmpty {
            conditions.append("kind IN (\(filter.kinds.map { _ in "?" }.joined(separator: ",")))")
            bindings.append(contentsOf: filter.kinds.map { String($0) })
        }
        if let year = filter.year {
            conditions.append("substr(captured_day, 1, 4) = ?")
            bindings.append(year)
        }
        if let city = filter.city {
            conditions.append("city = ?")
            bindings.append(city)
        }
        if let device = filter.device {
            conditions.append("device_key = ?")
            bindings.append(device)
        }
        if let theme = filter.theme {
            conditions.append("theme = ?")
            bindings.append(theme)
        }
        if filter.minRating > 0 {
            conditions.append("rating >= ?")
            bindings.append(String(filter.minRating))
        }
        if filter.onlyPick { conditions.append("is_pick = 1") }
        if filter.onlyLog { conditions.append("is_log = 1") }
        let term = filter.searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        if !term.isEmpty {
            conditions.append("(file_name LIKE ? OR keywords_flat LIKE ? OR city LIKE ? OR device_key LIKE ?)")
            let like = "%\(term)%"
            bindings.append(contentsOf: [like, like, like, like])
        }

        let orderBy: String
        switch sort {
        case .capturedDesc: orderBy = "COALESCE(captured_ms, 0) DESC, file_name ASC"
        case .capturedAsc: orderBy = "COALESCE(captured_ms, 0) ASC, file_name ASC"
        case .ratingDesc: orderBy = "rating DESC, COALESCE(captured_ms, 0) DESC"
        case .nameAsc: orderBy = "file_name ASC"
        case .sizeDesc: orderBy = "size_bytes DESC"
        }

        let whereClause = conditions.isEmpty ? "" : "WHERE " + conditions.joined(separator: " AND ")
        let sql = """
        SELECT id, full_path, file_name, kind, width, height, COALESCE(duration_seconds, 0),
               captured_ms, captured_day, country, province, city, device_key, camera_model,
               lens_model, theme, tone, rating, color_label, is_pick, is_log, log_profile,
               keywords_flat, thumb_key, lut_id, COALESCE(is_group_cover, 1)
        FROM assets
        \(whereClause)
        ORDER BY \(orderBy)
        LIMIT \(limit)
        """

        let rows = try query(sql: sql, bindings: bindings) { stmt -> Asset in
            Asset(
                id: text(stmt, 0),
                path: text(stmt, 1),
                fileName: text(stmt, 2),
                kind: Int(sqlite3_column_int64(stmt, 3)),
                width: Int(sqlite3_column_int64(stmt, 4)),
                height: Int(sqlite3_column_int64(stmt, 5)),
                durationSeconds: sqlite3_column_double(stmt, 6),
                capturedAt: date(ms: sqlite3_column_type(stmt, 7) == SQLITE_NULL ? nil : sqlite3_column_int64(stmt, 7)),
                capturedDay: optionalText(stmt, 8),
                country: optionalText(stmt, 9),
                province: optionalText(stmt, 10),
                city: optionalText(stmt, 11),
                deviceKey: optionalText(stmt, 12),
                cameraModel: optionalText(stmt, 13),
                lensModel: optionalText(stmt, 14),
                theme: optionalText(stmt, 15),
                tone: optionalText(stmt, 16),
                rating: Int(sqlite3_column_int64(stmt, 17)),
                colorLabel: optionalText(stmt, 18),
                isPick: sqlite3_column_int64(stmt, 19) != 0,
                isLog: sqlite3_column_int64(stmt, 20) != 0,
                logProfile: optionalText(stmt, 21),
                keywords: text(stmt, 22)
                    .split(separator: ",")
                    .map { $0.trimmingCharacters(in: .whitespaces) }
                    .filter { !$0.isEmpty },
                thumbKey: optionalText(stmt, 23),
                lutId: optionalText(stmt, 24),
                isGroupCover: sqlite3_column_int64(stmt, 25) != 0
            )
        }
        return rows
    }

    func totalCount() throws -> Int {
        try query(sql: "SELECT COUNT(*) FROM assets WHERE COALESCE(is_group_cover, 1) = 1", bindings: []) { stmt in
            Int(sqlite3_column_int64(stmt, 0))
        }.first ?? 0
    }

    func loadFacets() throws -> Facets {
        var facets = Facets()
        let cover = "COALESCE(is_group_cover, 1) = 1"

        facets.kinds = try query(sql: "SELECT kind, COUNT(*) FROM assets WHERE \(cover) GROUP BY kind ORDER BY COUNT(*) DESC", bindings: []) { stmt in
            KindFacet(kind: Int(sqlite3_column_int64(stmt, 0)), count: Int(sqlite3_column_int64(stmt, 1)))
        }

        facets.years = try query(sql: """
            SELECT substr(captured_day, 1, 4) AS y, COUNT(*) FROM assets
            WHERE \(cover) AND captured_day IS NOT NULL AND length(captured_day) >= 4
            GROUP BY y ORDER BY y DESC
            """, bindings: []) { stmt in
            FacetCount(value: text(stmt, 0), count: Int(sqlite3_column_int64(stmt, 1)))
        }

        facets.cities = try query(sql: """
            SELECT city, COUNT(*) FROM assets
            WHERE \(cover) AND city IS NOT NULL AND city <> ''
            GROUP BY city ORDER BY COUNT(*) DESC LIMIT 60
            """, bindings: []) { stmt in
            FacetCount(value: text(stmt, 0), count: Int(sqlite3_column_int64(stmt, 1)))
        }

        facets.devices = try query(sql: """
            SELECT device_key, COUNT(*) FROM assets
            WHERE \(cover) AND device_key IS NOT NULL AND device_key <> ''
            GROUP BY device_key ORDER BY COUNT(*) DESC LIMIT 60
            """, bindings: []) { stmt in
            FacetCount(value: text(stmt, 0), count: Int(sqlite3_column_int64(stmt, 1)))
        }

        facets.themes = try query(sql: """
            SELECT theme, COUNT(*) FROM assets
            WHERE \(cover) AND theme IS NOT NULL AND theme <> ''
            GROUP BY theme ORDER BY COUNT(*) DESC LIMIT 40
            """, bindings: []) { stmt in
            FacetCount(value: text(stmt, 0), count: Int(sqlite3_column_int64(stmt, 1)))
        }

        return facets
    }

    // MARK: SQLite 底层

    private func withDatabase<T>(_ body: (OpaquePointer) throws -> T) throws -> T {
        var db: OpaquePointer?
        let flags = SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX
        guard sqlite3_open_v2(dbPath, &db, flags, nil) == SQLITE_OK, let db else {
            let message = db.map { String(cString: sqlite3_errmsg($0)) } ?? "无法打开数据库"
            sqlite3_close(db)
            throw CatalogError.openFailed(message)
        }
        defer { sqlite3_close(db) }
        return try body(db)
    }

    private func query<T>(sql: String, bindings: [String], row: (OpaquePointer) throws -> T) throws -> [T] {
        try withDatabase { db in
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
                throw CatalogError.queryFailed(String(cString: sqlite3_errmsg(db)))
            }
            defer { sqlite3_finalize(stmt) }
            for (index, value) in bindings.enumerated() {
                sqlite3_bind_text(stmt, Int32(index + 1), value, -1, SQLITE_TRANSIENT_UNSAFE)
            }
            var results: [T] = []
            while sqlite3_step(stmt) == SQLITE_ROW {
                results.append(try row(stmt))
            }
            return results
        }
    }

    private func text(_ stmt: OpaquePointer, _ index: Int32) -> String {
        optionalText(stmt, index) ?? ""
    }

    private func optionalText(_ stmt: OpaquePointer, _ index: Int32) -> String? {
        guard sqlite3_column_type(stmt, index) != SQLITE_NULL,
              let pointer = sqlite3_column_text(stmt, index) else { return nil }
        return String(cString: pointer)
    }

    private func date(ms: Int64?) -> Date? {
        guard let ms, ms > 0 else { return nil }
        return Date(timeIntervalSince1970: Double(ms) / 1000.0)
    }
}

enum CatalogError: LocalizedError {
    case openFailed(String)
    case queryFailed(String)

    var errorDescription: String? {
        switch self {
        case .openFailed(let message): return "打开索引库失败：\(message)"
        case .queryFailed(let message): return "查询失败：\(message)"
        }
    }
}