import Photos
import SwiftUI
import UIKit

/// 系统相册里的一张素材（PhotoKit 的轻量投影）。
struct PhotoItem: Identifiable, Hashable {
    let id: String
    let creationDate: Date?
    let pixelWidth: Int
    let pixelHeight: Int
    let isFavorite: Bool
    let isVideo: Bool
    let durationSeconds: Double

    var shortDateText: String {
        guard let creationDate else { return "未知日期" }
        let f = DateFormatter()
        f.locale = Locale(identifier: "zh_CN")
        f.dateFormat = "MM-dd HH:mm"
        return f.string(from: creationDate)
    }

    var dateText: String {
        guard let creationDate else { return "未知日期" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "yyyy-MM-dd HH:mm"
        return formatter.string(from: creationDate)
    }

    var dimensionText: String {
        guard pixelWidth > 0, pixelHeight > 0 else { return "—" }
        return "\(pixelWidth) × \(pixelHeight)"
    }
}

/// iPad/iPhone 上的「系统相册」数据源：只读浏览 + 收藏切换，缩略图走 PhotoKit。
@MainActor
final class PhotoLibraryModel: ObservableObject {
    enum AccessState: Equatable {
        case unknown, denied, limited, authorized
    }

    @Published private(set) var access: AccessState = .unknown
    @Published private(set) var items: [PhotoItem] = []
    @Published private(set) var isLoading = false
    @Published var selectedPhotoID: String?
    @Published var message: String?

    private let imageManager = PHCachingImageManager()
    private var thumbnails: [String: UIImage] = [:]
    private var thumbnailOrder: [String] = []

    var selectedIndex: Int? {
        guard let selectedPhotoID else { return nil }
        return items.firstIndex { $0.id == selectedPhotoID }
    }

    // MARK: - 权限

    func requestAccessAndLoad() async {
        let status = PHPhotoLibrary.authorizationStatus(for: .readWrite)
        switch status {
        case .authorized: access = .authorized
        case .limited: access = .limited
        case .denied, .restricted: access = .denied
        case .notDetermined:
            let granted = await withCheckedContinuation { continuation in
                PHPhotoLibrary.requestAuthorization(for: .readWrite) { newStatus in
                    continuation.resume(returning: newStatus)
                }
            }
            switch granted {
            case .authorized: access = .authorized
            case .limited: access = .limited
            default: access = .denied
            }
        @unknown default:
            access = .denied
        }
        guard access == .authorized || access == .limited else { return }
        await load()
    }

    // MARK: - 读取

    func load() async {
        isLoading = true
        defer { isLoading = false }
        let options = PHFetchOptions()
        options.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: false)]
        options.predicate = NSPredicate(format: "mediaType == %d", PHAssetMediaType.image.rawValue)
        let result = PHAsset.fetchAssets(with: options)
        var next: [PhotoItem] = []
        next.reserveCapacity(min(result.count, 5000))
        result.enumerateObjects { asset, _, _ in
            next.append(PhotoItem(id: asset.localIdentifier,
                                  creationDate: asset.creationDate,
                                  pixelWidth: asset.pixelWidth,
                                  pixelHeight: asset.pixelHeight,
                                  isFavorite: asset.isFavorite,
                                  isVideo: asset.mediaType == .video,
                                  durationSeconds: asset.duration))
        }
        items = next
    }

    func refresh() async {
        guard access == .authorized || access == .limited else {
            await requestAccessAndLoad()
            return
        }
        await load()
    }

    // MARK: - 缩略图与大图

    /// 已缓存的缩略图（同步读取，供列表重绘时避免异步闪烁）。
    func cachedThumbnail(for item: PhotoItem) -> UIImage? {
        thumbnails[item.id]
    }

    func thumbnail(for item: PhotoItem, size: CGSize) -> UIImage? {
        if let cached = thumbnails[item.id] { return cached }
        guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [item.id], options: nil).firstObject else {
            return nil
        }
        let options = PHImageRequestOptions()
        options.deliveryMode = .opportunistic
        options.resizeMode = .fast
        options.isSynchronous = true
        options.isNetworkAccessAllowed = true
        var image: UIImage?
        imageManager.requestImage(for: asset, targetSize: size, contentMode: .aspectFill, options: options) { result, _ in
            image = result
        }
        if let image {
            thumbnails[item.id] = image
            thumbnailOrder.append(item.id)
            if thumbnailOrder.count > 400 {
                let removed = thumbnailOrder.removeFirst()
                thumbnails.removeValue(forKey: removed)
            }
        }
        return image
    }

    func thumbnailAsync(for item: PhotoItem, size: CGSize) async -> UIImage? {
        if let cached = thumbnails[item.id] { return cached }
        guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [item.id], options: nil).firstObject else {
            return nil
        }
        let image: UIImage? = await withCheckedContinuation { continuation in
            let options = PHImageRequestOptions()
            options.deliveryMode = .opportunistic
            options.resizeMode = .fast
            options.isNetworkAccessAllowed = true
            imageManager.requestImage(for: asset, targetSize: size, contentMode: .aspectFill, options: options) { result, info in
                let degraded = (info?[PHImageResultIsDegradedKey] as? Bool) ?? false
                if degraded { return }
                continuation.resume(returning: result)
            }
        }
        if let image {
            thumbnails[item.id] = image
            thumbnailOrder.append(item.id)
            if thumbnailOrder.count > 400 {
                let removed = thumbnailOrder.removeFirst()
                thumbnails.removeValue(forKey: removed)
            }
        }
        return image
    }

    func image(for item: PhotoItem, maxPixel: Int = 2400) async -> UIImage? {
        guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [item.id], options: nil).firstObject else {
            return nil
        }
        return await withCheckedContinuation { continuation in
            let options = PHImageRequestOptions()
            options.deliveryMode = .highQualityFormat
            options.resizeMode = .exact
            options.isNetworkAccessAllowed = true
            let scale = UIScreen.main.scale
            let target = CGSize(width: CGFloat(maxPixel) / scale, height: CGFloat(maxPixel) / scale)
            imageManager.requestImage(for: asset, targetSize: target, contentMode: .aspectFit, options: options) { result, info in
                let degraded = (info?[PHImageResultIsDegradedKey] as? Bool) ?? false
                if degraded { return }
                continuation.resume(returning: result)
            }
        }
    }


    /// 把相册原图导出到指定目录（供素材库导入），返回写入的文件 URL。
    func export(_ items: [PhotoItem], to directory: URL) async throws -> [URL] {
        let fm = FileManager.default
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        var written: [URL] = []
        for item in items {
            guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [item.id], options: nil).firstObject,
                  let resource = PHAssetResource.assetResources(for: asset).first(where: {
                      $0.type == .photo || $0.type == .fullSizePhoto || $0.type == .alternatePhoto
                  }) else { continue }
            let destination = uniqueURL(directory.appendingPathComponent(resource.originalFilename), fileManager: fm)
            do {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    PHAssetResourceManager.default().writeData(for: resource, toFile: destination, options: nil) { error in
                        if let error { continuation.resume(throwing: error) } else { continuation.resume(returning: ()) }
                    }
                }
                written.append(destination)
            } catch {
                message = "导出失败：\(resource.originalFilename) — \(error.localizedDescription)"
            }
        }
        return written
    }

    private func uniqueURL(_ url: URL, fileManager: FileManager) -> URL {
        guard fileManager.fileExists(atPath: url.path) else { return url }
        let base = url.deletingPathExtension().lastPathComponent
        let ext = url.pathExtension
        var index = 1
        while true {
            let candidate = url.deletingLastPathComponent()
                .appendingPathComponent("\(base)-\(index)")
                .appendingPathExtension(ext)
            if !fileManager.fileExists(atPath: candidate.path) { return candidate }
            index += 1
        }
    }

    // MARK: - 写操作

    func toggleFavorite(_ item: PhotoItem) async {
        guard access == .authorized || access == .limited else { return }
        guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [item.id], options: nil).firstObject else { return }
        do {
            try await PHPhotoLibrary.shared().performChanges {
                PHAssetChangeRequest(for: asset).isFavorite = !item.isFavorite
            }
            await load()
        } catch {
            message = "修改收藏失败：\(error.localizedDescription)"
        }
    }

    /// 预览/截图辅助：按索引直接打开某张（模拟器无法合成点击）。
    func selectFirstIfNeeded() {
        if selectedPhotoID == nil, let first = items.first {
            selectedPhotoID = first.id
        }
    }
}
