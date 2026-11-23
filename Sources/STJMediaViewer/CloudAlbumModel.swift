import SwiftUI
import Combine
import STJCore

/// 云相册（WebDAV）：远端目录里的图片/视频以相册网格呈现，可预览、导入、上传。
@MainActor
final class CloudAlbumModel: ObservableObject {
    @Published private(set) var items: [CloudAlbumItem] = []
    @Published private(set) var thumbnails: [String: CGImage] = [:]
    @Published var selection: Set<String> = []
    @Published private(set) var isLoading = false
    @Published private(set) var status: String?
    @Published private(set) var importProgress: (done: Int, total: Int)?
    @Published private(set) var isImporting = false
    @Published private(set) var previewItem: CloudAlbumItem?
    @Published private(set) var previewImage: CGImage?
    @Published private(set) var isPreviewLoading = false

    private var client: WebDAVClient?
    private var pendingThumbnails: [CloudAlbumItem] = []
    private var inFlight = 0
    private let maxThumbnailTasks = 6
    private var loadTask: Task<Void, Never>?
    private var previewTask: Task<Void, Never>?

    var selectedItems: [CloudAlbumItem] {
        items.filter { selection.contains($0.id) }
    }

    var countText: String {
        "\(items.count) 张云端素材"
    }

    func configure(credentials: WebDAVCredentials) {
        if client == nil {
            client = WebDAVClient(credentials: credentials)
        }
    }

    func load(credentials: WebDAVCredentials) {
        configure(credentials: credentials)
        guard let client else { return }
        loadTask?.cancel()
        isLoading = true
        status = "正在读取云端相册…"
        selection = []
        previewItem = nil
        cancelThumbnails()
        items = []
        thumbnails = [:]
        loadTask = Task { [weak self] in
            do {
                let listed = try await CloudAlbum.listMedia(client, root: "")
                guard let self, !Task.isCancelled else { return }
                self.items = listed
                if UserDefaults.standard.bool(forKey: "cloudAlbumSelectAllOnLaunch") {
                    self.selection = Set(listed.map(\.id))
                }
                self.isLoading = false
                self.status = listed.isEmpty ? "云端相册里还没有图片或视频。" : "共 \(listed.count) 张云端素材"
            } catch {
                guard let self else { return }
                self.isLoading = false
                self.status = "读取失败：\(error.localizedDescription)"
            }
        }
    }

    func refresh(credentials: WebDAVCredentials) {
        load(credentials: credentials)
    }

    // MARK: - 缩略图（限制并发，按需加载）

    func requestThumbnail(_ item: CloudAlbumItem) {
        guard thumbnails[item.id] == nil else { return }
        guard !pendingThumbnails.contains(where: { $0.id == item.id }) else { return }
        guard inFlight < maxThumbnailTasks else {
            pendingThumbnails.append(item)
            return
        }
        startThumbnail(item)
    }

    private func startThumbnail(_ item: CloudAlbumItem) {
        guard let client else { return }
        inFlight += 1
        Task { [weak self] in
            let image = await CloudAlbum.thumbnail(for: item, client: client, maxPixel: 480)
            guard let self else { return }
            self.inFlight -= 1
            if let image { self.thumbnails[item.id] = image }
            self.pumpThumbnails()
        }
    }

    private func pumpThumbnails() {
        while inFlight < maxThumbnailTasks, !pendingThumbnails.isEmpty {
            let next = pendingThumbnails.removeFirst()
            if thumbnails[next.id] == nil {
                startThumbnail(next)
            }
        }
    }

    private func cancelThumbnails() {
        pendingThumbnails = []
        inFlight = 0
    }

    // MARK: - 预览

    func openPreview(_ item: CloudAlbumItem) {
        previewItem = item
        previewImage = nil
        guard let client else { return }
        isPreviewLoading = true
        previewTask?.cancel()
        previewTask = Task { [weak self] in
            let image = await CloudAlbum.previewImage(for: item, client: client, maxPixel: 2400)
            guard let self, !Task.isCancelled else { return }
            self.previewImage = image
            self.isPreviewLoading = false
        }
    }

    func closePreview() {
        previewTask?.cancel()
        previewTask = nil
        previewItem = nil
        previewImage = nil
        isPreviewLoading = false
    }

    // MARK: - 导入

    func download(_ items: [CloudAlbumItem], to destinationRoot: URL) async throws -> CloudAlbumImportSummary {
        guard let client else { throw CloudAlbumError.noMedia }
        isImporting = true
        importProgress = (0, items.count)
        defer {
            isImporting = false
            importProgress = nil
        }
        let summary = try await CloudAlbum.importItems(items,
                                                       client: client,
                                                       destinationRoot: destinationRoot,
                                                       progress: { [weak self] progress in
            Task { @MainActor in
                self?.importProgress = (progress.completed, progress.total)
            }
        })
        return summary
    }
}