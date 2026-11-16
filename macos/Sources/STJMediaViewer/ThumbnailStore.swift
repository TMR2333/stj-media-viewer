// Copyright (c) Stjarna Perspective. MIT.
//
// 缩略图：优先用 Windows 版已经生成好的缓存文件
// （缓存命名规则 `{thumb_key}_{revision}_{size}.jpg`），没有的话退回 Quick Look 生成。

import AppKit
import QuickLookThumbnailing

final class ThumbnailStore {
    private(set) var cacheDirectory: URL?
    private var byKey: [String: URL] = [:]

    /// 缓存目录变化时重建索引：每个 thumb_key 取尺寸最大的那张（同一素材可能有 320/360 多份）。
    func configure(cacheDirectory: URL?) {
        byKey.removeAll()
        cacheDirectory = cacheDirectory
        guard let cacheDirectory,
              let files = try? FileManager.default.contentsOfDirectory(
                at: cacheDirectory,
                includingPropertiesForKeys: nil,
                options: [.skipsHiddenFiles]) else { return }

        for file in files where file.pathExtension.lowercased() == "jpg" {
            guard let (key, size) = Self.parse(fileName: file.lastPathComponent) else { continue }
            if let existing = byKey[key],
               let existingSize = Self.parse(fileName: existing.lastPathComponent)?.size,
               existingSize >= size {
                continue
            }
            byKey[key] = file
        }
    }

    /// 从 `{40位sha1}_{revision}_{size}[_lut].jpg` 里取 key 和尺寸。
    static func parse(fileName: String) -> (key: String, size: Int)? {
        let parts = fileName.split(separator: "_")
        guard parts.count >= 3,
              parts[0].count == 40,
              let size = Int(String(parts[2].prefix(while: { $0.isNumber }))) else { return nil }
        return (String(parts[0]).lowercased(), size)
    }

    func cachedURL(forKey key: String?) -> URL? {
        guard let key, !key.isEmpty else { return nil }
        return byKey[key.lowercased()]
    }

    var cachedCount: Int { byKey.count }

    /// Quick Look 兜底（视频、未生成缓存的图片）。
    static func quickLookImage(for url: URL, size: CGFloat) async -> NSImage? {
        let request = QLThumbnailGenerator.Request(
            fileAt: url,
            size: CGSize(width: size, height: size),
            scale: 2,
            representationTypes: .thumbnail)

        return await withCheckedContinuation { continuation in
            QLThumbnailGenerator.shared.generateBestRepresentation(for: request) { representation, _ in
                continuation.resume(returning: representation?.nsImage)
            }
        }
    }
}