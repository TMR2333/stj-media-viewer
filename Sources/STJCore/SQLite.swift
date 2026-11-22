import Foundation
import SQLite3

private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

public enum DatabaseError: LocalizedError {
    case open(String)
    case prepare(String)
    case step(String)
    case notFound(String)

    public var errorDescription: String? {
        switch self {
        case .open(let message): return "打开数据库失败：\(message)"
        case .prepare(let message): return "准备语句失败：\(message)"
        case .step(let message): return "执行语句失败：\(message)"
        case .notFound(let message): return message
        }
    }
}

/// 极简 SQLite 封装：只为 catalog.db 的读写服务，无第三方依赖。
public final class Database {
    private var handle: OpaquePointer?
    private let lock = NSRecursiveLock()
    public let path: String

    public init(path: String, readOnly: Bool) throws {
        self.path = path
        var db: OpaquePointer?
        let flags = readOnly ? SQLITE_OPEN_READONLY : (SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE)
        let rc = sqlite3_open_v2(path, &db, flags | SQLITE_OPEN_FULLMUTEX, nil)
        guard rc == SQLITE_OK, let db else {
            let message = db.map { String(cString: sqlite3_errmsg($0)) } ?? "SQLITE_ERROR \(rc)"
            sqlite3_close(db)
            throw DatabaseError.open(message)
        }
        handle = db
        sqlite3_busy_timeout(db, 3000)
        // 不强制切换 journal_mode：Windows 版素材库默认 delete 模式，
        // 强行改成 WAL 会让之后只读打开（缺少 -wal/-shm 时）失败。
        if !readOnly {
            try? execute("PRAGMA foreign_keys=ON;")
        }
    }

    deinit {
        if let handle {
            sqlite3_close(handle)
        }
    }

    private func sqliteMessage() -> String {
        guard let handle else { return "数据库已关闭" }
        return String(cString: sqlite3_errmsg(handle))
    }

    public var lastInsertRowID: Int64 {
        sqlite3_last_insert_rowid(handle)
    }

    public func execute(_ sql: String) throws {
        lock.lock()
        defer { lock.unlock() }
        var errorMessage: UnsafeMutablePointer<CChar>?
        let rc = sqlite3_exec(handle, sql, nil, nil, &errorMessage)
        if rc != SQLITE_OK {
            let message = errorMessage.map { String(cString: $0) } ?? sqliteMessage()
            sqlite3_free(errorMessage)
            throw DatabaseError.step(message)
        }
    }

    public func prepare(_ sql: String) throws -> Statement {
        lock.lock()
        defer { lock.unlock() }
        var stmt: OpaquePointer?
        let rc = sqlite3_prepare_v2(handle, sql, -1, &stmt, nil)
        guard rc == SQLITE_OK, let stmt else {
            throw DatabaseError.prepare("\(sqliteMessage()) [\(sql)]")
        }
        return Statement(handle: stmt, database: self)
    }

    public func transaction<T>(_ body: () throws -> T) throws -> T {
        lock.lock()
        defer { lock.unlock() }
        try execute("BEGIN IMMEDIATE;")
        do {
            let value = try body()
            try execute("COMMIT;")
            return value
        } catch {
            try? execute("ROLLBACK;")
            throw error
        }
    }

    // MARK: 便捷查询

    public func query<T>(_ sql: String, binds: [Any?] = [], map: (Statement) -> T) throws -> [T] {
        let statement = try prepare(sql)
        try statement.bindAll(binds)
        var rows: [T] = []
        while try statement.step() {
            rows.append(map(statement))
        }
        statement.reset()
        return rows
    }

    public func queryOne<T>(_ sql: String, binds: [Any?] = [], map: (Statement) -> T) throws -> T? {
        try query(sql, binds: binds, map: map).first
    }

    public func scalarInt(_ sql: String, binds: [Any?] = []) throws -> Int {
        try queryOne(sql, binds: binds) { $0.int(0) ?? 0 } ?? 0
    }

    public func tableColumns(_ table: String) throws -> Set<String> {
        try query("PRAGMA table_info(\(table));") { statement in
            statement.string(1) ?? ""
        }.reduce(into: Set<String>()) { $0.insert($1) }
    }

    public func tableExists(_ table: String) throws -> Bool {
        try scalarInt("SELECT count(*) FROM sqlite_master WHERE type IN ('table','view') AND name = ?;", binds: [table]) > 0
    }
}

public final class Statement {
    private var handle: OpaquePointer?
    private weak var database: Database?
    private var closed = false

    init(handle: OpaquePointer, database: Database) {
        self.handle = handle
        self.database = database
    }

    deinit {
        reset()
        if let handle {
            sqlite3_finalize(handle)
        }
    }

    public func bindAll(_ values: [Any?]) throws {
        for (offset, value) in values.enumerated() {
            try bind(index: Int32(offset + 1), value)
        }
    }

    public func bind(index: Int32, _ value: Any?) throws {
        let rc: Int32
        switch value {
        case nil:
            rc = sqlite3_bind_null(handle, index)
        case let value as Int:
            rc = sqlite3_bind_int64(handle, index, Int64(value))
        case let value as Int64:
            rc = sqlite3_bind_int64(handle, index, value)
        case let value as Bool:
            rc = sqlite3_bind_int64(handle, index, value ? 1 : 0)
        case let value as Double:
            rc = sqlite3_bind_double(handle, index, value)
        case let value as String:
            rc = sqlite3_bind_text(handle, index, value, -1, SQLITE_TRANSIENT)
        case let value as Data:
            rc = value.withUnsafeBytes { buffer in
                sqlite3_bind_blob(handle, index, buffer.baseAddress, Int32(buffer.count), SQLITE_TRANSIENT)
            }
        default:
            rc = sqlite3_bind_text(handle, index, "\(value)", -1, SQLITE_TRANSIENT)
        }
        guard rc == SQLITE_OK else {
            throw DatabaseError.step("绑定参数失败 \(rc)")
        }
    }

    @discardableResult
    public func step() throws -> Bool {
        guard let handle else { throw DatabaseError.step("语句已结束") }
        let rc = sqlite3_step(handle)
        switch rc {
        case SQLITE_ROW: return true
        case SQLITE_DONE: return false
        default:
            throw DatabaseError.step(String(cString: sqlite3_errmsg(sqlite3_db_handle(handle))))
        }
    }

    public func reset() {
        guard !closed, let handle else { return }
        sqlite3_reset(handle)
        sqlite3_clear_bindings(handle)
    }

    /// 查询结果第 `index` 列的列名（用于计算列）。
    public func name(at index: Int32) -> String? {
        guard let cString = sqlite3_column_name(handle, index) else { return nil }
        return String(cString: cString)
    }

    public func columnCount() -> Int32 { sqlite3_column_count(handle) }

    public func isNull(_ index: Int32) -> Bool {
        sqlite3_column_type(handle, index) == SQLITE_NULL
    }

    public func int(_ index: Int32) -> Int? {
        guard !isNull(index) else { return nil }
        return Int(sqlite3_column_int64(handle, index))
    }

    public func int64(_ index: Int32) -> Int64? {
        guard !isNull(index) else { return nil }
        return sqlite3_column_int64(handle, index)
    }

    public func double(_ index: Int32) -> Double? {
        guard !isNull(index) else { return nil }
        return sqlite3_column_double(handle, index)
    }

    public func bool(_ index: Int32) -> Bool? {
        int(index).map { $0 != 0 }
    }

    public func string(_ index: Int32) -> String? {
        guard !isNull(index), let cString = sqlite3_column_text(handle, index) else { return nil }
        return String(cString: cString)
    }
}