import Foundation

/// 一组同名的 JPEG + RAW 文件（任意一侧可以为空）。
public struct ExportPair: Equatable {
    public var jpeg: URL?
    public var raw: URL?

    public init(jpeg: URL? = nil, raw: URL? = nil) {
        self.jpeg = jpeg
        self.raw = raw
    }

    public var urls: [URL] {
        [jpeg, raw].compactMap { $0 }
    }

    public var isEmpty: Bool { urls.isEmpty }
}

/// 把「同一目录、同一基名」的 JPEG 与 RAW 当作一对素材，用于配对导出。
public enum PairExporter {
    public static let rawExtensions: Set<String> = [
        "cr3", "cr2", "crw", "nef", "nrw", "arw", "srf", "sr2", "raf", "rw2",
        "orf", "dng", "pef", "srw", "x3f", "rwl", "mos", "mrw", "3fr", "erf",
        "mef", "iiq", "k25", "kdc", "dcr", "raw"
    ]

    public static let jpegExtensions: Set<String> = ["jpg", "jpeg"]

    /// 在素材所在目录里找出配对的 JPEG / RAW。
    /// - Parameter fileManager: 便于测试注入。
    public static func plan(for asset: Asset, fileManager: FileManager = .default) -> ExportPair {
        let url = asset.fileURL
        guard !asset.isVideo else { return ExportPair(jpeg: nil, raw: nil) }
        let ext = url.pathExtension.lowercased()
        var pair = ExportPair()
        if rawExtensions.contains(ext) {
            pair.raw = url
        } else if jpegExtensions.contains(ext) {
            pair.jpeg = url
        } else {
            return pair
        }

        let directory = url.deletingLastPathComponent()
        let stem = url.deletingPathExtension().lastPathComponent
        guard let entries = try? fileManager.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: nil,
            options: [.skipsHiddenFiles]
        ) else {
            return pair
        }

        for entry in entries where entry.deletingPathExtension().lastPathComponent.caseInsensitiveCompare(stem) == .orderedSame {
            let siblingExt = entry.pathExtension.lowercased()
            if rawExtensions.contains(siblingExt) {
                if pair.raw == nil { pair.raw = entry }
            } else if jpegExtensions.contains(siblingExt) {
                if pair.jpeg == nil { pair.jpeg = entry }
            }
        }
        return pair
    }

    /// 复制到目标文件夹；同名已存在时自动加 `-1`、`-2` 后缀，返回实际写入的 URL。
    @discardableResult
    public static func copy(_ pairs: [ExportPair],
                            to directory: URL,
                            fileManager: FileManager = .default) throws -> [URL] {
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        var written: [URL] = []
        for pair in pairs {
            for source in pair.urls {
                let destination = uniqueDestination(for: source.lastPathComponent, in: directory, fileManager: fileManager)
                try fileManager.copyItem(at: source, to: destination)
                written.append(destination)
            }
        }
        return written
    }

    static func uniqueDestination(for fileName: String,
                                  in directory: URL,
                                  fileManager: FileManager) -> URL {
        let base = (fileName as NSString).deletingPathExtension
        let ext = (fileName as NSString).pathExtension
        var candidate = directory.appendingPathComponent(fileName)
        var index = 1
        while fileManager.fileExists(atPath: candidate.path) {
            let name = ext.isEmpty ? "\(base)-\(index)" : "\(base)-\(index).\(ext)"
            candidate = directory.appendingPathComponent(name)
            index += 1
        }
        return candidate
    }
}