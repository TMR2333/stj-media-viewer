// Copyright (c) Stjarna Perspective. MIT.
//
// 界面状态：数据目录、筛选、选中项、加载。

import AppKit
import Combine
import Foundation

@MainActor
final class LibraryModel: ObservableObject {
    @Published var assets: [Asset] = []
    @Published var facets = Facets()
    @Published var filter = LibraryFilter() {
        didSet { if filter != oldValue { scheduleReload() } }
    }
    @Published var sort: SortKey = .capturedDesc {
        didSet { if sort != oldValue { scheduleReload() } }
    }
    @Published var selectedID: Asset.ID?
    @Published var isLoading = false
    @Published var errorMessage: String?
    @Published var dataDirectory: URL?
    @Published var totalCount = 0

    let thumbnails = ThumbnailStore()

    private var catalog: Catalog?
    private var reloadTask: Task<Void, Never>?

    init() {
        for candidate in Catalog.defaultCandidates() {
            if let db = Catalog.validate(directory: candidate) {
                open(dbPath: db.path)
                break
            }
        }
        if catalog == nil {
            errorMessage = nil   // 首次启动：显示空状态里的"选择数据目录"引导
        }
    }

    var selectedAsset: Asset? {
        guard let selectedID else { return nil }
        return assets.first { $0.id == selectedID }
    }

    var cacheDescription: String {
        guard let dataDirectory else { return "未连接素材库数据" }
        let cache = dataDirectory.appendingPathComponent("Cache/thumbs")
        return "\(thumbnails.cachedCount) 张缓存缩略图 · \(cache.path)"
    }

    // MARK: 数据目录

    func chooseDataDirectory() {
        let panel = NSOpenPanel()
        panel.title = "选择 STJ Media Viewer 数据目录（包含 catalog.db）"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "使用此目录"
        panel.directoryURL = dataDirectory

        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let db = Catalog.validate(directory: url) else {
            errorMessage = "这个目录里没有 catalog.db —— 请选择含 catalog.db 的数据目录（通常是 Windows 上的 %LOCALAPPDATA%\\StjMediaViewer）。"
            return
        }
        open(dbPath: db.path)
        UserDefaults.standard.set(url.path, forKey: "dataDirectory")
        reload(immediate: true)
    }

    private func open(dbPath: String) {
        let catalog = Catalog(dbPath: dbPath)
        self.catalog = catalog
        dataDirectory = URL(fileURLWithPath: dbPath).deletingLastPathComponent()
        thumbnails.configure(cacheDirectory: catalog.thumbnailDirectory)
        errorMessage = nil
        reload(immediate: true)
    }

    // MARK: 加载

    func reload(immediate: Bool = false) {
        reloadTask?.cancel()
        guard let catalog else { return }

        let filter = filter
        let sort = sort
        isLoading = true

        reloadTask = Task { [weak self] in
            if !immediate {
                try? await Task.sleep(nanoseconds: 120_000_000)   // 输入防抖
            }
            guard !Task.isCancelled else { return }

            do {
                let payload = try await Task.detached(priority: .userInitiated) {
                    () throws -> ([Asset], Facets, Int) in
                    let assets = try catalog.loadAssets(filter: filter, sort: sort)
                    let facets = try catalog.loadFacets()
                    let total = try catalog.totalCount()
                    return (assets, facets, total)
                }.value

                let (assets, facets, total) = payload
                guard !Task.isCancelled else { return }
                self?.assets = assets
                self?.facets = facets
                self?.totalCount = total
                self?.isLoading = false
            } catch {
                guard !Task.isCancelled else { return }
                self?.errorMessage = error.localizedDescription
                self?.isLoading = false
            }
        }
    }

    private func scheduleReload() { reload() }

    // MARK: 筛选操作

    func toggleKind(_ kind: Int) {
        if filter.kinds.contains(kind) { filter.kinds.remove(kind) } else { filter.kinds.insert(kind) }
    }

    func clearFilters() {
        filter = LibraryFilter(onlyCover: filter.onlyCover)
    }

    func revealInFinder() {
        guard let asset = selectedAsset else { return }
        NSWorkspace.shared.activateFileViewerSelecting([asset.fileURL])
    }

    func openInDefaultApp() {
        guard let asset = selectedAsset else { return }
        NSWorkspace.shared.open(asset.fileURL)
    }

    var kindSummary: String {
        if filter.kinds.isEmpty { return "全部类型" }
        return filter.kinds.sorted().map { kind in
            switch kind {
            case 1: return "图片"
            case 2: return "视频"
            case 3: return "音频"
            default: return "其他"
            }
        }.joined(separator: " + ")
    }
}