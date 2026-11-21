import Foundation

public struct LibraryBackupInfo: Codable, Sendable {
    public var createdAt: Date
    public var appVersion: String
    public var sourcePath: String
    public var fileCount: Int
    public var byteCount: Int
}

public struct LibraryBackupSummary: Sendable {
    public var info: LibraryBackupInfo
    public var destination: URL
    public var duration: TimeInterval
    public var descriptionText: String {
        "备份 \(info.fileCount) 个文件 · \(ByteCountFormatter.string(fromByteCount: Int64(info.byteCount), countStyle: .file)) · \(String(format: "%.1f", duration)) 秒"
    }
}

public struct LibraryRestoreSummary: Sendable {
    public var restoredFiles: Int
    public var databaseReplaced: Bool
    public var previousDatabaseBackup: URL?
    public var descriptionText: String {
        var text = "恢复 \(restoredFiles) 个文件"
        if databaseReplaced { text += " · 已替换 catalog.db" }
        if previousDatabaseBackup != nil { text += " · 旧库已改名保留" }
        return text
    }
}

public enum LibraryBackupError: Error, LocalizedError {
    case missingCatalog(URL)
    case invalidBackup(URL)
    case notALibrary(URL)

    public var errorDescription: String? {
        switch self {
        case .missingCatalog(let url): return "找不到 catalog.db：\(url.path)"
        case .invalidBackup(let url): return "不是有效的备份目录：\(url.path)"
        case .notALibrary(let url): return "目标不是素材库目录：\(url.path)"
        }
    }
}

/// 素材库备份 / 恢复：整库目录复制（catalog.db 及其 WAL 边车 + 缩略图缓存）。
public enum LibraryBackup {
    public static let infoFileName = "backup-info.json"
    public static let thumbsRelativePath = "Cache/thumbs"

    /// 在 `destination` 下创建带时间戳的备份目录，返回备份摘要。
    public static func create(from library: URL,
                              destination: URL,
                              appVersion: String = "dev",
                              fileManager: FileManager = .default) throws -> LibraryBackupSummary {
        let started = Date()
        let database = library.appendingPathComponent("catalog.db")
        guard fileManager.fileExists(atPath: database.path) else {
            throw LibraryBackupError.missingCatalog(library)
        }
        let stamp = Self.stampFormatter.string(from: Date())
        let folder = destination.appendingPathComponent("STJMediaViewer-Backup-\(stamp)", isDirectory: true)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)

        var files = 0
        var bytes = 0
        for suffix in ["", "-wal", "-shm"] {
            let source = URL(fileURLWithPath: database.path + suffix)
            guard fileManager.fileExists(atPath: source.path) else { continue }
            let target = folder.appendingPathComponent("catalog.db" + suffix)
            try fileManager.copyItem(at: source, to: target)
            files += 1
            bytes += size(of: target, fileManager: fileManager)
        }
        let thumbs = library.appendingPathComponent(thumbsRelativePath, isDirectory: true)
        if fileManager.fileExists(atPath: thumbs.path) {
            let target = folder.appendingPathComponent(thumbsRelativePath, isDirectory: true)
            try fileManager.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
            try fileManager.copyItem(at: thumbs, to: target)
            let counts = try countFiles(in: target, fileManager: fileManager)
            files += counts.files
            bytes += counts.bytes
        }
        let info = LibraryBackupInfo(createdAt: Date(), appVersion: appVersion,
                                     sourcePath: library.path, fileCount: files, byteCount: bytes)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(info).write(to: folder.appendingPathComponent(infoFileName))
        return LibraryBackupSummary(info: info, destination: folder, duration: Date().timeIntervalSince(started))
    }

    /// 把备份目录恢复到 `library`：替换 catalog.db（旧文件改名保留），覆盖缩略图缓存。
    public static func restore(from backup: URL,
                               to library: URL,
                               fileManager: FileManager = .default) throws -> LibraryRestoreSummary {
        let backedUpDatabase = backup.appendingPathComponent("catalog.db")
        guard fileManager.fileExists(atPath: backedUpDatabase.path) else {
            throw LibraryBackupError.invalidBackup(backup)
        }
        try fileManager.createDirectory(at: library, withIntermediateDirectories: true)
        let database = library.appendingPathComponent("catalog.db")
        var previous: URL?
        if fileManager.fileExists(atPath: database.path) {
            let stamp = Self.stampFormatter.string(from: Date())
            let candidate = library.appendingPathComponent("catalog.db.restore-\(stamp)")
            try? fileManager.removeItem(at: candidate)
            try fileManager.moveItem(at: database, to: candidate)
            previous = candidate
        }
        // 清理旧 WAL 边车，避免与恢复的库不一致
        for suffix in ["-wal", "-shm"] {
            let stale = URL(fileURLWithPath: database.path + suffix)
            try? fileManager.removeItem(at: stale)
        }
        try fileManager.copyItem(at: backedUpDatabase, to: database)
        for suffix in ["-wal", "-shm"] {
            let source = backup.appendingPathComponent("catalog.db" + suffix)
            guard fileManager.fileExists(atPath: source.path) else { continue }
            let target = URL(fileURLWithPath: database.path + suffix)
            try? fileManager.removeItem(at: target)
            try fileManager.copyItem(at: source, to: target)
        }
        var restored = 1
        let backupThumbs = backup.appendingPathComponent(thumbsRelativePath, isDirectory: true)
        let libraryThumbs = library.appendingPathComponent(thumbsRelativePath, isDirectory: true)
        if fileManager.fileExists(atPath: backupThumbs.path) {
            try fileManager.createDirectory(at: libraryThumbs.deletingLastPathComponent(), withIntermediateDirectories: true)
            if fileManager.fileExists(atPath: libraryThumbs.path) {
                try? fileManager.removeItem(at: libraryThumbs)
            }
            try fileManager.copyItem(at: backupThumbs, to: libraryThumbs)
            restored += try countFiles(in: libraryThumbs, fileManager: fileManager).files
        }
        return LibraryRestoreSummary(restoredFiles: restored, databaseReplaced: true, previousDatabaseBackup: previous)
    }

    /// 读取备份目录里的 backup-info.json（不存在时返回 nil）。
    public static func info(in backup: URL, fileManager: FileManager = .default) -> LibraryBackupInfo? {
        let url = backup.appendingPathComponent(infoFileName)
        guard let data = try? Data(contentsOf: url) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(LibraryBackupInfo.self, from: data)
    }

    private static let stampFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        return formatter
    }()

    private static func size(of url: URL, fileManager: FileManager) -> Int {
        (try? fileManager.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
    }

    private static func countFiles(in directory: URL, fileManager: FileManager) throws -> (files: Int, bytes: Int) {
        guard let enumerator = fileManager.enumerator(at: directory, includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey]) else {
            return (0, 0)
        }
        var files = 0, bytes = 0
        for case let url as URL in enumerator {
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
            guard values.isRegularFile == true else { continue }
            files += 1
            bytes += values.fileSize ?? 0
        }
        return (files, bytes)
    }
}
