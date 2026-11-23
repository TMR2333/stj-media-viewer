import Foundation
import Observation
import STJCore

/// iPad/iPhone 版素材库模型。
///
/// 与 macOS 版共享 `STJCore`（SQLite / 筛选 / 缩略图 / 导入），但文件访问走 iOS 的
/// 安全作用域书签（security-scoped bookmark）：用户用「文件」App 选一次目录，
/// 之后每次启动解析书签即可继续读写该目录内的 catalog.db 与媒体文件。
@MainActor
final class MobileLibraryModel: ObservableObject {
    @Published private(set) var catalog: Catalog?
    @Published private(set) var directory: URL?
    @Published private(set) var assets: [Asset] = []
    @Published private(set) var totalCount = 0
    @Published private(set) var facets = Facets()
    @Published private(set) var collections: [AssetCollection] = []
    @Published private(set) var isLoading = false
    @Published private(set) var isImporting = false
    @Published private(set) var importHint = ""
    @Published var errorMessage: String?

    @Published var filter = AssetFilter() { didSet { scheduleReload() } }
    @Published var sort = SortDescriptor(key: .captured, ascending: false) { didSet { scheduleReload() } }
    @Published var selection: Set<String> = []
    @Published var density: LibraryDensity = .medium { didSet { settings.density = density } }
    @Published var showsInspector = true
    @Published var galleryAssetID: String?

    let settings = SettingsStore.shared

    private var reloadTask: Task<Void, Never>?
    private var securityScopedURL: URL?
    private static let bookmarkKey = "libraryFolderBookmark"

    init() {
        density = settings.density
        filter.foldRawPairs = settings.foldRawPairs
    }

    // MARK: - 库的打开与导入

    /// 恢复上次打开、仍然可用的素材库。
    func restoreLastLibrary() async {
        guard directory == nil else { return }
        if let bookmark = UserDefaults.standard.data(forKey: Self.bookmarkKey) {
            var stale = false
            #if os(macOS)
            let resolveOptions: URL.BookmarkResolutionOptions = [.withSecurityScope]
            #else
            let resolveOptions: URL.BookmarkResolutionOptions = []
            #endif
            if let url = try? URL(resolvingBookmarkData: bookmark,
                                  options: resolveOptions,
                                  relativeTo: nil,
                                  bookmarkDataIsStale: &stale) {
                do {
                    try await open(directory: url)
                    if stale { saveBookmark(for: url) }
                    return
                } catch {
                    errorMessage = "无法重新打开上次的素材库：\(error.localizedDescription)"
                }
            }
        }
        if let url = Self.defaultLibraryURL() {
            try? await open(directory: url)
        }
    }

    /// 用「文件」App 选择包含 catalog.db 的目录。
    func open(directory url: URL) async throws {
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        let catalog = try Catalog(directory: url)
        self.catalog = catalog
        self.directory = url
        ThumbnailStore.shared.configure(cacheDirectory: url.appendingPathComponent("Cache/thumbs"))
        saveBookmark(for: url)
        await reload(reason: "打开素材库")
    }

    /// 导入 / 扫描一个目录（P0：把选中的目录当作素材库根）。
    func importLibrary(from url: URL) async {
        isImporting = true
        importHint = "正在扫描…"
        defer { isImporting = false }
        let root = url
        do {
            let scoped = root.startAccessingSecurityScopedResource()
            defer { if scoped { root.stopAccessingSecurityScopedResource() } }
            let summary = try await Task.detached(priority: .userInitiated) {
                try Importer.importFolder(root, libraryName: root.lastPathComponent) { progress in
                    Task { @MainActor in
                        self.importHint = "已处理 \(progress.processed)/\(progress.total)：\(progress.currentFileName)"
                    }
                }
            }.value
            importHint = summary.descriptionText
            try await open(directory: root)
        } catch {
            errorMessage = "导入失败：\(error.localizedDescription)"
        }
    }

    // MARK: - 查询

    private func scheduleReload(delay: Duration = .milliseconds(150)) {
        reloadTask?.cancel()
        reloadTask = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            await self?.reload(reason: "筛选变化")
        }
    }

    func reload(reason: String) async {
        guard let catalog else { return }
        isLoading = true
        defer { isLoading = false }
        var query = filter
        query.foldRawPairs = settings.foldRawPairs
        let sort = self.sort
        do {
            let snapshot = try await Task.detached(priority: .userInitiated) {
                (try catalog.loadAssets(filter: query, sort: sort),
                 try catalog.count(filter: query),
                 try catalog.loadFacets(filter: query),
                 try catalog.collections())
            }.value
            assets = snapshot.0
            totalCount = snapshot.1
            facets = snapshot.2
            collections = snapshot.3
            let visible = Set(assets.map(\.id))
            selection = selection.filter { visible.contains($0) }
        } catch {
            errorMessage = "读取素材库失败：\(error.localizedDescription)"
        }
    }

    // MARK: - 选择与编辑

    var selectedAssets: [Asset] { assets.filter { selection.contains($0.id) } }
    var primarySelection: Asset? { selectedAssets.first }

    func select(_ asset: Asset) { selection = [asset.id] }

    func update(_ patch: AssetPatch, ids: [String]) {
        guard let catalog, !ids.isEmpty else { return }
        do {
            try catalog.update(patch: patch, ids: ids)
            Task { await reload(reason: "修改素材") }
        } catch {
            errorMessage = "写入失败：\(error.localizedDescription)"
        }
    }

    func setRating(_ rating: Int) {
        let ids = Array(selection)
        guard !ids.isEmpty else { return }
        var patch = AssetPatch()
        patch.rating = rating
        update(patch, ids: ids)
    }

    func setPick(_ value: Int) {
        let ids = Array(selection)
        guard !ids.isEmpty else { return }
        var patch = AssetPatch()
        patch.isPick = .set(value)
        update(patch, ids: ids)
    }

    /// 偏好变化（RAW+JPG 折叠等）后重新查询。
    func reloadFilterPreferences() {
        var query = filter
        query.foldRawPairs = settings.foldRawPairs
        filter = query
    }

    // MARK: - 书签

    private func saveBookmark(for url: URL) {
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        #if os(macOS)
        let options: URL.BookmarkCreationOptions = [.withSecurityScope]
        #else
        let options: URL.BookmarkCreationOptions = []
        #endif
        guard let data = try? url.bookmarkData(options: options,
                                               includingResourceValuesForKeys: nil,
                                               relativeTo: nil) else { return }
        UserDefaults.standard.set(data, forKey: Self.bookmarkKey)
    }

    /// 无书签时（模拟器/调试）退回 App 文档目录里的 DemoLibrary。
    static func defaultLibraryURL() -> URL? {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
        guard let docs else { return nil }
        let candidate = docs.appendingPathComponent("DemoLibrary", isDirectory: true)
        return FileManager.default.fileExists(atPath: candidate.appendingPathComponent("catalog.db").path) ? candidate : nil
    }
}
