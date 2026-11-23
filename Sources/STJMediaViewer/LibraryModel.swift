import AppKit
import Combine
import STJCore
import SwiftUI

enum SidebarSelection: Hashable {
    case section(SidebarSection)
    case collection(String)
    case mergeGroup(String)
}

@MainActor
final class LibraryModel: ObservableObject {
    // MARK: 状态

    @Published private(set) var catalog: Catalog?
    @Published private(set) var dataDirectory: URL?
    @Published private(set) var assets: [Asset] = []
    @Published private(set) var totalCount = 0
    @Published private(set) var facets = Facets()
    @Published private(set) var collections: [AssetCollection] = []
    @Published private(set) var mergeGroups: [MergeGroup] = []
    @Published private(set) var libraries: [LibraryRoot] = []
    @Published var filter = AssetFilter() {
        didSet {
            if filter != oldValue {
                scheduleReload(reason: "筛选变化")
            }
        }
    }
    @Published var sort = SortDescriptor() {
        didSet {
            if sort != oldValue {
                scheduleReload(reason: "排序变化")
            }
        }
    }
    @Published var selection: Set<String> = []
    @Published var sidebarSelection: SidebarSelection = .section(.all) {
        didSet {
            guard sidebarSelection != oldValue else { return }
            applySidebarSelection()
        }
    }
    @Published var expandedGroup: MergeGroup? {
        didSet {
            AppLog.breadcrumb(expandedGroup.map { "展开合并组：\($0.name)（\($0.count) 项）" } ?? "收起合并组")
            scheduleReload(reason: "合并组")
        }
    }
    @Published var isLoading = false
    @Published var cachedThumbnailCount = 0
    @Published var quickLookFallbackCount = 0
    @Published var columnVisibility: NavigationSplitViewVisibility = .all
    @Published var statusMessage: String?
    /// 检查更新的结果（有新版时非 nil）。
    @Published var availableUpdate: UpdateInfo?
    @Published var isCheckingUpdate = false
    @Published var updateCheckMessage: String?
    // MARK: - 目录标签页（不再要求目录里存在 catalog.db）

    /// 一个「目录标签页」：直接把某个图片目录作为一个库打开，素材文件保持原地不动。
    struct FolderTab: Identifiable, Hashable {
        let directory: URL
        var id: String { WorkspacePaths.libraryID(for: directory) }
        var name: String { directory.lastPathComponent }
        var path: String { directory.path }
    }

    @Published var folderTabs: [FolderTab] = []
    @Published var activeFolderTabID: String?

    private static let folderTabsKey = "folderTabs"
    private static let activeFolderTabKey = "activeFolderTab"
    private var workspaceCatalog: Catalog?

    var activeFolderTab: FolderTab? {
        guard let activeFolderTabID else { return nil }
        return folderTabs.first { $0.id == activeFolderTabID }
    }

    /// 应用支持目录里的工作区数据库（所有目录共用一个，素材目录不生成任何文件）。
    private func ensureWorkspaceCatalog() throws -> Catalog {
        if let workspaceCatalog, FileManager.default.fileExists(atPath: workspaceCatalog.databaseURL.path) {
            return workspaceCatalog
        }
        let url = WorkspacePaths.databaseURL
        if !FileManager.default.fileExists(atPath: url.path) {
            let db = try Database(path: url.path, readOnly: false)
            try CatalogSchema.create(in: db)
        }
        let catalog = try Catalog(databaseURL: url)
        workspaceCatalog = catalog
        return catalog
    }

    private func persistFolderTabs() {
        UserDefaults.standard.set(folderTabs.map(\.path), forKey: Self.folderTabsKey)
        UserDefaults.standard.set(activeFolderTabID, forKey: Self.activeFolderTabKey)
    }

    private func restoreFolderTabsIfNeeded() {
        guard folderTabs.isEmpty else { return }
        let defaults = UserDefaults.standard
        let paths = defaults.stringArray(forKey: Self.folderTabsKey) ?? []
        let existing = paths
            .map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
            .filter { FileManager.default.fileExists(atPath: $0.path) }
        folderTabs = existing.map { FolderTab(directory: $0) }
        if let active = defaults.string(forKey: Self.activeFolderTabKey),
           folderTabs.contains(where: { $0.id == active }) {
            activeFolderTabID = active
        }
    }

    @Published var presentedError: String?
    @Published var isBatchEditorPresented = false
    @Published var searchFocused = false
    @Published var listDensityOverride: LibraryDensity?
    @Published var showsTimeline = false
    @Published var showsMap = false
    /// 画廊里当前预览的 LUT 名称；nil 表示原图。
    @Published var galleryLUTName: String?

    /// 数据目录 `LUTs/` 下的 .cube 预设。
    var availableLUTs: [CubeLUT] {
        LUTDirectory.loadAll(in: dataDirectory)
    }

    /// 对所选素材跑 AI 分类（OpenAI 兼容接口），把结果写回素材库。
    func aiTagSelection() {
        guard let catalog, !selectedAssets.isEmpty, !isAITagging else { return }
        #if DEBUG
        let apiKey = UserDefaults.standard.string(forKey: "aiAPIKey") ?? settings.apiKey ?? ""
        #else
        let apiKey = settings.apiKey ?? ""
        #endif
        guard !apiKey.isEmpty else {
            showStatus("请先在设置 → AI 里填写 API Key")
            return
        }
        let configuration = AITagger.Configuration(endpoint: settings.aiEndpoint,
                                                  model: settings.aiModel,
                                                  apiKey: apiKey)
        let tagger = AITagger(configuration: configuration)
        let assets = selectedAssets
        isAITagging = true
        aiTagProgress = (0, assets.count)
        AppLog.breadcrumb("AI 打标开始：\(assets.count) 个素材 → \(settings.aiEndpoint)")
        Task {
            var applied = 0
            var failed = 0
            var firstError: String?
            for (index, asset) in assets.enumerated() {
                if Task.isCancelled { break }
                if let data = await AIProgress.previewJPEGData(for: asset) {
                    do {
                        let suggestion = try await tagger.suggest(imageData: data)
                        if suggestion.error == nil {
                            let patch = AITagger.patch(for: asset, suggestion: suggestion)
                            if !patch.isEmpty {
                                try catalog.update(patch: patch, ids: [asset.id])
                                applied += 1
                            }
                        } else {
                            failed += 1
                            firstError = firstError ?? suggestion.error
                        }
                    } catch {
                        failed += 1
                        firstError = firstError ?? error.localizedDescription
                    }
                } else {
                    failed += 1
                }
                aiTagProgress = (index + 1, assets.count)
            }
            aiTagProgress = nil
            isAITagging = false
            aiTagSummary = "AI 打标：成功 \(applied) · 失败 \(failed)"
            showStatus(aiTagSummary ?? "")
            AppLog.breadcrumb("AI 打标完成：成功 \(applied) · 失败 \(failed)")
            if let firstError, applied == 0 {
                presentedError = firstError
            }
            await reloadNow(reason: "AI 打标", debounced: false)
        }
    }

    func toggleTimeline() {
        showsTimeline.toggle()
        if showsTimeline { showsMap = false }
    }

    func toggleMap() {
        showsMap.toggle()
        if showsMap { showsTimeline = false }
    }

    // 重复项检测
    @Published var isDuplicateScanning = false
    @Published var duplicateGroups: [DuplicateGroup] = []
    @Published var isDuplicateSheetPresented = false

    // 整理动作
    @Published var isOrganizing = false
    // 设置与图片数据导入导出
    @Published var isExchangeRunning = false
    // MARK: 云同步
    @Published var isAITagging = false
    @Published var aiTagProgress: (done: Int, total: Int)?
    @Published var aiTagSummary: String?
    @Published var isCloudSyncing = false
    @Published var cloudSyncProgress: CloudSyncProgress?
    @Published var cloudSyncSummary: String?
    @Published var cloudDiagnostics: [DiagnosticStep]?
    @Published var isCloudAlbumPresented = false
    private var cloudSyncTask: Task<Void, Never>?
    @Published var organizeSummary: String?
    /// 画廊模式当前查看的素材 ID；非 nil 时弹出画廊窗口。
    @Published var galleryAssetID: String?

    private var reloadTask: Task<Void, Never>?
    private var statusTask: Task<Void, Never>?
    private var started = false
    private var currentReloadReason = ""

    var settings: SettingsStore { SettingsStore.shared }

    // MARK: 派生属性

    var density: LibraryDensity {
        get { listDensityOverride ?? settings.density }
        set {
            listDensityOverride = newValue
            settings.density = newValue
            AppLog.breadcrumb("切换视图密度：\(newValue.title)")
        }
    }

    var selectedAssets: [Asset] {
        assets.filter { selection.contains($0.id) }
    }

    var primarySelection: Asset? {
        selectedAssets.first
    }

    var galleryAsset: Asset? {
        guard let galleryAssetID else { return nil }
        return assets.first { $0.id == galleryAssetID }
    }

    /// 画廊里当前素材在可见列表中的序号（从 1 开始），用于「3 / 25」提示。
    var galleryPosition: (index: Int, total: Int)? {
        guard let galleryAssetID, let index = assets.firstIndex(where: { $0.id == galleryAssetID }) else {
            return nil
        }
        return (index + 1, assets.count)
    }

    var canDeleteSelection: Bool { !selection.isEmpty }

    var breadcrumb: String {
        var pieces: [String] = ["素材库"]
        switch sidebarSelection {
        case .section(let section):
            pieces.append(section.title)
        case .collection(let id):
            pieces.append(collections.first { $0.id == id }?.name ?? "合集")
        case .mergeGroup(let id):
            pieces.append(mergeGroups.first { $0.id == id }?.name ?? "合并组")
        }
        if !filter.ratings.isEmpty { pieces.append("评分 \(filter.ratings.sorted().map(String.init).joined(separator: "/"))") }
        if !filter.years.isEmpty { pieces.append("年份 \(filter.years.sorted().joined(separator: "/"))") }
        if !filter.cities.isEmpty { pieces.append("城市 \(filter.cities.sorted().joined(separator: "/"))") }
        if !filter.themes.isEmpty { pieces.append("主题 \(filter.themes.sorted().joined(separator: "/"))") }
        if !filter.searchText.trimmingCharacters(in: .whitespaces).isEmpty { pieces.append("搜索“\(filter.searchText)”") }
        if let expandedGroup { pieces.append("合并组 \(expandedGroup.name)") }
        return pieces.joined(separator: " / ")
    }

    var statusLine: String {
        var pieces: [String] = []
        pieces.append("共 \(totalCount) 张")
        if assets.count != totalCount { pieces.append("当前显示 \(assets.count) 张") }
        if !selection.isEmpty { pieces.append("已选 \(selection.count) 项") }
        if settings.foldRawPairs { pieces.append("已折叠 RAW+JPG 配对") }
        pieces.append("缩略图缓存命中 \(cachedThumbnailCount) 张")
        if quickLookFallbackCount > 0 { pieces.append("QuickLook 兜底 \(quickLookFallbackCount) 张") }
        if let dataDirectory { pieces.append("数据目录 \(dataDirectory.lastPathComponent)") }
        return pieces.joined(separator: " · ")
    }

    // MARK: 启动与打开

    func startIfNeeded() async {
        guard !started else { return }
        started = true
        let defaults = UserDefaults.standard
        if let path = defaults.string(forKey: "dataDirectory"), !path.isEmpty {
            await open(directory: URL(fileURLWithPath: (path as NSString).expandingTildeInPath), quiet: true)
        } else {
            restoreFolderTabsIfNeeded()
            if let active = activeFolderTab {
                await switchFolderTab(active.id)
                AppLog.breadcrumb("启动：恢复目录标签 \(active.name)")
            } else if let last = defaults.string(forKey: "lastFolderDirectory"), !last.isEmpty,
                      FileManager.default.fileExists(atPath: last) {
                await openFolder(URL(fileURLWithPath: last), quiet: true)
            } else if let last = LastLibraryStore.lastDirectoryPath {
                await open(directory: URL(fileURLWithPath: last), quiet: true)
            } else {
                AppLog.breadcrumb("启动：未指定目录，等待用户选择")
            }
        }
        if let preselect = defaults.string(forKey: "preselectAssetID"), !preselect.isEmpty {
            selection = [preselect]
            AppLog.breadcrumb("启动参数预置选中项：\(preselect)")
        }
        if let gallery = defaults.string(forKey: "galleryAssetID"), !gallery.isEmpty {
            selection = [gallery]
            galleryAssetID = gallery
            AppLog.breadcrumb("启动参数打开画廊：\(gallery)")
        }
        #if DEBUG
        if defaults.bool(forKey: "showsTimeline") {
            showsTimeline = true
            AppLog.breadcrumb("启动参数切换时间线视图")
        }
        if defaults.bool(forKey: "showsMap") {
            showsMap = true
            AppLog.breadcrumb("启动参数切换地图视图")
        }
        if let lutName = defaults.string(forKey: "galleryLUT"), !lutName.isEmpty {
            galleryLUTName = lutName
            AppLog.breadcrumb("启动参数画廊 LUT：\(lutName)")
        }
        // 仅 Debug 构建：验收截图用，强制显示首次引导。
        if defaults.bool(forKey: "showOnboardingOnLaunch") {
            settings.hasCompletedOnboarding = false
            AppLog.breadcrumb("启动参数显示首次引导")
        }
        // 仅 Debug 构建：验收用，直接跑一次素材库备份（跳过面板）。
        if let backupPath = defaults.string(forKey: "backupOnLaunch"), !backupPath.isEmpty, let library = dataDirectory {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 800_000_000)
                do {
                    let summary = try await Task.detached(priority: .userInitiated) {
                        try LibraryBackup.create(from: library, destination: URL(fileURLWithPath: backupPath), appVersion: "debug")
                    }.value
                    showStatus("备份完成：\(summary.descriptionText)")
                    AppLog.breadcrumb("启动参数备份：\(summary.descriptionText) → \(summary.destination.path)")
                } catch {
                    AppLog.error("启动参数备份失败：\(error.localizedDescription)")
                }
            }
        }
        // 仅 Debug 构建：验收用，直接从备份恢复（跳过面板与确认框）。
        if let restorePath = defaults.string(forKey: "restoreOnLaunch"), !restorePath.isEmpty, let library = dataDirectory {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 800_000_000)
                do {
                    let summary = try await Task.detached(priority: .userInitiated) {
                        try LibraryBackup.restore(from: URL(fileURLWithPath: restorePath), to: library)
                    }.value
                    showStatus("恢复完成：\(summary.descriptionText)")
                    AppLog.breadcrumb("启动参数恢复：\(summary.descriptionText)")
                    await open(directory: library)
                } catch {
                    AppLog.error("启动参数恢复失败：\(error.localizedDescription)")
                }
            }
        }
        // 仅 Debug 构建：验收截图用，把当前素材全部导出为带水印 JPEG。
        if let exportPath = defaults.string(forKey: "watermarkExportDirectory"), !exportPath.isEmpty {
            let style = settings.watermarkStyle
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 1_200_000_000)
                let list = assets
                let summary = await Task.detached(priority: .userInitiated) {
                    try? Watermarker.export(assets: list, to: URL(fileURLWithPath: exportPath), style: style)
                }.value
                if let summary {
                    showStatus("水印导出：\(summary.descriptionText)")
                    AppLog.breadcrumb("启动参数水印导出：\(summary.descriptionText) → \(exportPath)")
                } else {
                    AppLog.error("启动参数水印导出失败")
                }
            }
        }
        // 仅 Debug 构建：本机无辅助功能授权，无法合成菜单点击，用启动参数直接打开重复项面板以便验收截图。
        #if DEBUG
        if defaults.bool(forKey: "cloudAlbumOnLaunch") {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 900_000_000)
                self.presentCloudAlbum()
            }
        }
        if let text = defaults.string(forKey: "searchTextOnLaunch"), !text.isEmpty {
            var next = filter
            next.searchText = text
            filter = next
        }
        #endif
        if defaults.bool(forKey: "showDuplicatesOnLaunch") {
            findDuplicates()
        }
        // 仅 Debug 构建：自检多文件拖拽的载荷（选中 3 张，打印实际会拖出的文件）。
        if defaults.bool(forKey: "dragPayloadLogOnLaunch") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
                guard let self, let first = self.assets.first else { return }
                self.selection = Set(self.assets.prefix(3).map { $0.id })
                let urls = self.dragURLs(for: first)
                AppLog.info("拖拽载荷自检：选中 \(self.selection.count) 张 → 拖拽 \(urls.count) 个文件：\(urls.map { $0.lastPathComponent }.joined(separator: ", "))")
            }
        }
        // 仅 Debug 构建：验收截图用，全选素材并直接开始 AI 打标（无需点菜单）。
        if defaults.bool(forKey: "aiTagAllOnLaunch") {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 600_000_000)
                selectAll()
                aiTagSelection()
                AppLog.breadcrumb("启动参数触发 AI 打标：\(selection.count) 项")
            }
        }
        // 仅 Debug 构建：验收截图用，全选素材并直接开始 WebDAV 上传（无需点菜单）。
        if defaults.bool(forKey: "uploadAllOnLaunch") {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 500_000_000)
                selectAll()
                uploadSelectionToWebDAV()
                AppLog.breadcrumb("启动参数触发全量 WebDAV 上传：\(selection.count) 项")
            }
        }
        #endif
    }

    /// 打开一个目录（默认走「直接识别图片」的工作区模式，不需要 catalog.db）。
    func openFolder(_ folder: URL, quiet: Bool = false) async {
        let dir = folder.resolvingSymlinksInPath().standardizedFileURL
        let tab = FolderTab(directory: dir)
        if !folderTabs.contains(where: { $0.id == tab.id }) {
            folderTabs.append(tab)
        }
        activeFolderTabID = tab.id
        persistFolderTabs()
        UserDefaults.standard.set(dir.path, forKey: "lastFolderDirectory")
        await activate(tab, quiet: quiet)
    }

    /// 切换到已打开的目录标签页。
    func switchFolderTab(_ id: String) async {
        guard let tab = folderTabs.first(where: { $0.id == id }) else { return }
        guard id != activeFolderTabID || catalog == nil else { return }
        activeFolderTabID = id
        persistFolderTabs()
        await activate(tab, quiet: true)
    }

    /// 激活某个目录标签：打开工作区库 → 首次访问时索引 → 按目录过滤并刷新。
    private func activate(_ tab: FolderTab, quiet: Bool) async {
        do {
            let catalog = try ensureWorkspaceCatalog()
            self.catalog = catalog
            self.dataDirectory = WorkspacePaths.directory
            ThumbnailStore.shared.configure(cacheDirectory: WorkspacePaths.thumbnailRoot)
            cachedThumbnailCount = ThumbnailStore.shared.cachedThumbnailCount()

            var libraryID = (try? catalog.libraryID(forDirectory: tab.directory)) ?? tab.id
            var scoped = filter
            scoped.libraryIDs = [libraryID]
            let known = (try? catalog.count(filter: scoped)) ?? 0
            if known == 0 {
                if !quiet { showStatus("正在索引 \(tab.name)…") }
                let summary = try await Task.detached(priority: .userInitiated) {
                    try Importer.importFolder(tab.directory,
                                              databaseURL: WorkspacePaths.databaseURL,
                                              cacheDirectory: WorkspacePaths.thumbnailRoot,
                                              libraryName: tab.name)
                }.value
                AppLog.breadcrumb("索引目录：\(tab.directory.path) → 新增 \(summary.added)、跳过 \(summary.skipped)、缩略图 \(summary.thumbnails)")
                libraryID = (try? catalog.libraryID(forDirectory: tab.directory)) ?? libraryID
            }
            filter.libraryIDs = [libraryID]
            selection = []
            await reloadNow(reason: "切换目录 \(tab.name)")
            if !quiet {
                showStatus("已索引 \(tab.name)：\(totalCount) 张")
            }
        } catch {
            AppLog.error("打开目录失败：\(tab.directory.path) — \(error.localizedDescription)")
            presentedError = error.localizedDescription
        }
    }

    /// 关闭标签页（只从标签栏移除，不删除磁盘上的任何东西）。
    func closeFolderTab(_ id: String) {
        guard let index = folderTabs.firstIndex(where: { $0.id == id }) else { return }
        folderTabs.remove(at: index)
        if activeFolderTabID == id {
            let next = folderTabs.indices.contains(index) ? folderTabs[index] : folderTabs.last
            if let next {
                Task { await switchFolderTab(next.id) }
            } else {
                activeFolderTabID = nil
                filter.libraryIDs = []
                selection = []
                catalog = nil
                assets = []
                totalCount = 0
            }
        }
        persistFolderTabs()
    }

    /// 选择一个（或多个）图片目录，作为新标签页打开并索引。
    func openFolderFromPanel(multiple: Bool = true) async {
        let panel = NSOpenPanel()
        panel.title = "打开图片文件夹"
        panel.message = "选择一个图片目录，直接索引其中的图片与视频（不会在目录里写任何素材库文件）"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = multiple
        panel.prompt = "打开"
        if let activeFolderTab { panel.directoryURL = activeFolderTab.directory.deletingLastPathComponent() }
        guard panel.runModal() == .OK else { return }
        for url in panel.urls {
            await openFolder(url)
        }
    }

    /// 兼容入口：若目录里有 catalog.db 则按旧「共享素材库文件」方式打开；否则按目录索引。
    func open(directory: URL, quiet: Bool = false) async {
        let standardized = directory.standardizedFileURL
        if FileManager.default.fileExists(atPath: standardized.appendingPathComponent("catalog.db").path) {
            await openLegacyCatalog(directory: standardized, quiet: quiet)
        } else {
            await openFolder(standardized, quiet: quiet)
        }
    }

    private func openLegacyCatalog(directory: URL, quiet: Bool = false) async {
        do {
            activeFolderTabID = nil
            filter.libraryIDs = []
            let catalog = try Catalog(directory: directory)
            self.catalog = catalog
            self.dataDirectory = directory
            ThumbnailStore.shared.configure(cacheDirectory: directory.appendingPathComponent("Cache/thumbs"))
            LastLibraryStore.lastDirectoryPath = directory.path
            cachedThumbnailCount = ThumbnailStore.shared.cachedThumbnailCount()
            let libraryCount = (try? catalog.loadFacets(filter: AssetFilter()).total) ?? 0
            if !quiet {
                AppLog.breadcrumb("打开素材库：\(directory.path)（\(libraryCount) 张）")
                showStatus("已打开 \(directory.lastPathComponent)")
            }
            selection = []
            await reloadNow(reason: "打开素材库")
        } catch {
            AppLog.error("打开素材库失败：\(directory.path) — \(error.localizedDescription)")
            presentedError = error.localizedDescription
        }
    }

    func openFromPanel() async {
        await openFolderFromPanel()
    }

    // MARK: 导入 / 扫描（P2）

    /// 扫描导入进度（nil 表示当前没有导入任务）。
    @Published var importProgress: ImportProgress?
    @Published var isImporting = false

    func importLibraryFolder() {
        let panel = NSOpenPanel()
        panel.title = "导入 / 扫描素材目录"
        panel.message = "选择素材目录：没有 catalog.db 时会按 Windows 兼容 schema 新建素材库；已有库则增量扫描"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "扫描并导入"
        if let dataDirectory {
            panel.directoryURL = dataDirectory.deletingLastPathComponent()
        }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        importFrom(url)
    }

    func importFrom(_ folder: URL) {
        guard !isImporting else { return }
        isImporting = true
        importProgress = ImportProgress(processed: 0, total: 0, currentFileName: "")
        showStatus("正在扫描：\(folder.lastPathComponent)")
        let name = folder.lastPathComponent
        let progressHandler: @Sendable (ImportProgress) -> Void = { progress in
            Task { @MainActor [weak self] in
                self?.importProgress = progress
            }
        }
        Task {
            do {
                let summary = try await Task.detached(priority: .userInitiated) {
                    try Importer.importFolder(folder, libraryName: name, progress: progressHandler)
                }.value
                isImporting = false
                importProgress = nil
                showStatus("导入完成：\(summary.descriptionText)")
                AppLog.breadcrumb("导入素材库：\(folder.path) → \(summary.descriptionText)（\(String(format: "%.1f", summary.duration)) 秒）")
                if let databaseURL = summary.databaseURL {
                    await open(directory: databaseURL.deletingLastPathComponent(), quiet: true)
                }
            } catch {
                isImporting = false
                importProgress = nil
                presentedError = error.localizedDescription
                AppLog.error("导入失败：\(folder.path) — \(error.localizedDescription)")
            }
        }
    }

    // MARK: 刷新

    func scheduleReload(reason: String) {
        currentReloadReason = reason
        reloadTask?.cancel()
        reloadTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 120_000_000)
            guard !Task.isCancelled else { return }
            await self?.reloadNow(reason: reason, debounced: true)
        }
    }

    func reloadNow(reason: String = "手动刷新", debounced: Bool = false) async {
        guard let catalog else { return }
        isLoading = true
        defer { isLoading = false }
        var filter = self.filter
        filter.foldRawPairs = settings.foldRawPairs
        let sort = self.sort
        let groupID = expandedGroup?.id
        do {
            let snapshot = try await Task.detached(priority: .userInitiated) {
                let assets = try catalog.loadAssets(filter: filter, sort: sort, expandedGroupID: groupID)
                let total = try catalog.count(filter: filter, expandedGroupID: groupID)
                let facets = try catalog.loadFacets(filter: filter, expandedGroupID: groupID)
                let collections = try catalog.collections()
                let groups = try catalog.mergeGroups()
                let libraries = try catalog.libraries()
                return Snapshot(assets: assets, total: total, facets: facets,
                                collections: collections, mergeGroups: groups, libraries: libraries)
            }.value
            guard !Task.isCancelled else { return }
            assets = snapshot.assets
            totalCount = snapshot.total
            facets = snapshot.facets
            collections = snapshot.collections
            mergeGroups = snapshot.mergeGroups
            libraries = snapshot.libraries
            let visibleIDs = Set(snapshot.assets.map(\.id))
            selection = selection.filter { visibleIDs.contains($0) }
            cachedThumbnailCount = ThumbnailStore.shared.cachedThumbnailCount()
            quickLookFallbackCount = countQuickLookFallbacks(in: snapshot.assets)
            AppLog.breadcrumb("\(reason)：显示 \(snapshot.assets.count)/\(snapshot.total) 张 · \(breadcrumb)")
        } catch {
            AppLog.error("刷新失败：\(error.localizedDescription)")
            presentedError = error.localizedDescription
        }
    }

    private func countQuickLookFallbacks(in assets: [Asset]) -> Int {
        let hits = ThumbnailStore.shared.cachedHitCount(for: assets)
        return max(assets.count - hits, 0)
    }

    // MARK: 侧栏

    private func applySidebarSelection() {
        var next = AssetFilter()
        next.searchText = filter.searchText
        switch sidebarSelection {
        case .section(.all):
            break
        case .section(.images):
            next.kinds = [1]
        case .section(.videos):
            next.kinds = [2]
        case .section(.pick):
            next.pick = 1
        case .section(.rejected):
            next.pick = 0
        case .section(.uncategorized):
            next.unsetDimensions.insert(.collection)
        case .collection(let id):
            next.collectionIDs = [id]
        case .mergeGroup(let id):
            next.mergeGroupIDs = [id]
        }
        expandedGroup = nil
        selection = []
        filter = next
        AppLog.breadcrumb("切换侧栏：\(breadcrumb)")
    }

    func clearFilters() {
        filter = AssetFilter()
        selection = []
        showStatus("已清除筛选条件")
    }

    func removeFilterChip(_ apply: (inout AssetFilter) -> Void) {
        var next = filter
        apply(&next)
        filter = next
    }

    // MARK: 选择

    func handleTap(asset: Asset, modifiers: NSEvent.ModifierFlags) {
        if modifiers.contains(.command) {
            if selection.contains(asset.id) {
                selection.remove(asset.id)
            } else {
                selection.insert(asset.id)
            }
        } else if modifiers.contains(.shift), let anchor = primarySelection,
                  let anchorIndex = assets.firstIndex(where: { $0.id == anchor.id }),
                  let targetIndex = assets.firstIndex(where: { $0.id == asset.id }) {
            let range = anchorIndex < targetIndex ? anchorIndex...targetIndex : targetIndex...anchorIndex
            selection = Set(assets[range].map(\.id))
        } else {
            selection = [asset.id]
        }
        AppLog.breadcrumb("选中 \(selection.count) 项：\(asset.fileName)")
    }

    func selectAll() {
        selection = Set(assets.map(\.id))
        AppLog.breadcrumb("全选 \(selection.count) 项")
    }

    func clearSelection() {
        selection = []
    }

    func moveSelection(offset: Int) {
        guard !assets.isEmpty else { return }
        let currentIndex: Int
        if let first = selectedAssets.first, let index = assets.firstIndex(of: first) {
            currentIndex = index
        } else {
            currentIndex = offset > 0 ? -1 : assets.count
        }
        let nextIndex = min(max(currentIndex + offset, 0), assets.count - 1)
        selection = [assets[nextIndex].id]
    }

    // MARK: 交互动作

    func toggleQuickLook() {
        let urls = selectedAssets.map(\.fileURL)
        guard !urls.isEmpty else {
            AppLog.breadcrumb("空格快速查看：当前没有选中素材")
            return
        }
        AppLog.breadcrumb("空格快速查看 \(urls.count) 项")
        QuickLookController.shared.toggle(items: urls)
    }

    func openSelectionInDefaultApp() {
        let urls = selectedAssets.map(\.fileURL)
        guard !urls.isEmpty else { return }
        for url in urls {
            NSWorkspace.shared.open(url)
        }
        AppLog.breadcrumb("用默认应用打开 \(urls.count) 项")
        showStatus("已交给默认应用打开 \(urls.count) 个文件")
    }

    func revealSelection() {
        let urls = selectedAssets.map(\.fileURL)
        guard !urls.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(urls)
        AppLog.breadcrumb("在 Finder 中显示 \(urls.count) 项")
    }

    func deleteSelection() {
        guard let catalog else { return }
        let ids = selectedAssets.map(\.id)
        guard !ids.isEmpty else { return }
        let alert = NSAlert()
        alert.messageText = "把 \(ids.count) 个素材移到废纸篓？"
        let totalBytes = selectedAssets.reduce(Int64(0)) { $0 + $1.sizeBytes }
        let sizeText = ByteCountFormatter.string(fromByteCount: totalBytes, countStyle: .file)
        alert.informativeText = "共 \(sizeText)。原文件会进入废纸篓（可还原），并从 catalog.db 中移除记录。"
        alert.addButton(withTitle: "移到废纸篓")
        alert.addButton(withTitle: "取消")
        alert.alertStyle = .warning
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        Task {
            do {
                let expanded = try catalog.expandedIDs(for: ids)
                let urls = selectedAssets.map(\.fileURL)
                try await Self.recycleFiles(urls)
                try catalog.delete(ids: expanded)
                AppLog.breadcrumb("移到废纸篓：\(ids.count) 项（含合并组成员 \(expanded.count) 条记录）")
                showStatus("已移到废纸篓 \(ids.count) 项")
                selection = []
                await reloadNow(reason: "删除素材")
            } catch {
                AppLog.error("删除失败：\(error.localizedDescription)")
                presentedError = error.localizedDescription
            }
        }
    }

    private static func recycleFiles(_ urls: [URL]) async throws {
        guard !urls.isEmpty else { return }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            NSWorkspace.shared.recycle(urls) { _, error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume()
                }
            }
        }
    }

    // MARK: 编辑

    func update(_ patch: AssetPatch, ids: [String]) {
        guard let catalog, !ids.isEmpty else { return }
        Task {
            do {
                let expanded = try catalog.expandedIDs(for: ids)
                try catalog.update(patch: patch, ids: expanded)
                AppLog.breadcrumb("修改 \(ids.count) 项（含合并组成员共 \(expanded.count) 条记录）")
                showStatus("已更新 \(ids.count) 项")
                await reloadNow(reason: "编辑素材")
            } catch {
                AppLog.error("修改失败：\(error.localizedDescription)")
                presentedError = error.localizedDescription
            }
        }
    }

    func submitBatchEdit(_ patch: AssetPatch) {
        let ids = selectedAssets.map(\.id)
        update(patch, ids: ids)
        isBatchEditorPresented = false
    }

    func setRating(_ rating: Int) {
        var patch = AssetPatch()
        patch.rating = rating
        update(patch, ids: selectedAssets.map(\.id))
    }

    /// 设置色标（nil 表示清除）。对齐 Windows 右键「颜色标签」与 6–9 快捷键。
    func setColorLabel(_ label: String?) {
        var patch = AssetPatch()
        patch.colorLabel = label.map { PatchValue.set($0) } ?? .clear
        update(patch, ids: selection.isEmpty ? [] : Array(selection))
    }

    /// 设置影调（对齐 Windows 检视面板「影调」单选）。
    func setTone(_ tone: String?) {
        var patch = AssetPatch()
        patch.tone = tone.map { PatchValue.set($0) } ?? .clear
        update(patch, ids: selection.isEmpty ? [] : Array(selection))
    }

    /// 在画廊中打开当前选中项（Windows 双击卡片的行为）。
    func openSelectionInGallery() {
        guard let asset = primarySelection ?? selectedAssets.first else { return }
        openGallery(asset)
    }

    func setPick(_ value: Int?) {
        var patch = AssetPatch()
        patch.isPick = value.map { .set($0) } ?? .clear
        update(patch, ids: selectedAssets.map(\.id))
    }

    func writeXMP() {
        let targets = selectedAssets
        guard !targets.isEmpty else { return }
        Task {
            do {
                let written = try XMPWriter.write(for: targets)
                AppLog.breadcrumb("写入 XMP 侧车 \(written.count) 个")
                showStatus("已写入 \(written.count) 个 XMP 侧车文件")
            } catch {
                AppLog.error("写入 XMP 失败：\(error.localizedDescription)")
                presentedError = error.localizedDescription
            }
        }
    }

    // MARK: 配对导出

    /// 把当前选中的素材（含配对的 RAW/JPG 伴生文件）复制到用户选择的文件夹。
    // MARK: - 设置与图片数据导入导出（跨平台，macOS 与 Windows 通用格式）

    /// 导出当前应用设置 + 图片数据设置（评分/色标/选用/地点/主题/影调/关键词/标题/调色状态）
    /// 与合集归属，写成与 Windows 版通用的 JSON 文件。
    func exportSettingsExchange() {
        guard let catalog else { showStatus("先打开一个素材库"); return }
        let panel = NSSavePanel()
        panel.title = "导出设置与图片数据"
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "\(ExchangeFormat.defaultFileNamePrefix)-\(Self.exchangeTimestamp()).json"
        panel.prompt = "导出"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let document = try makeExchangeDocument(catalog: catalog)
            let data = try SettingsExchange.encode(document)
            try data.write(to: url, options: .atomic)
            let message = "已导出：设置 + \(document.assets.count) 条图片数据 + \(document.collections.count) 个合集"
            showStatus(message)
            AppLog.breadcrumb("导出设置与图片数据：\(message) → \(url.path)")
        } catch {
            presentedError = error.localizedDescription
            AppLog.error("导出设置与图片数据失败：\(error.localizedDescription)")
        }
    }

    private func makeExchangeDocument(catalog: Catalog) throws -> ExchangeDocument {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        return try SettingsExchange.exportDocument(catalog: catalog,
                                                   appVersion: version,
                                                   settings: SettingsExchange.snapshot(from: settings))
    }

    /// 从跨平台 JSON 文件导入设置与图片数据（可选择只填空字段或覆盖已有值）。
    func importSettingsExchange() {
        guard let catalog else { showStatus("先打开一个素材库"); return }
        let panel = NSOpenPanel()
        panel.title = "导入设置与图片数据"
        panel.allowedContentTypes = [.json]
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.prompt = "导入"
        guard panel.runModal() == .OK, let url = panel.url else { return }

        do {
            let data = try Data(contentsOf: url)
            let document = try SettingsExchange.decode(data)
            let plan = SettingsExchange.plan(document: document, assets: try catalog.loadAssets(limit: 200_000))

            let alert = NSAlert()
            alert.messageText = "导入设置与图片数据？"
            var lines: [String] = []
            lines.append("来源：\(document.app.name) \(document.app.version)（\(document.app.platform)） · \(document.app.exportedAt)")
            lines.append("可匹配素材 \(plan.matchedCount) 条，未匹配 \(plan.unmatchedCount) 条，合集 \(plan.collections.count) 个。")
            lines.append(plan.settings.isEmpty ? "文件中不含应用设置。" : "文件包含应用设置（外观/密度/排序/面板/RAW 折叠）。")
            lines.append("只导入图片数据与设置，不移动任何图片文件；密钥类设置不会包含在交换文件中。")
            alert.informativeText = lines.joined(separator: "\n")
            alert.addButton(withTitle: "导入（只填空字段）")
            alert.addButton(withTitle: "导入（覆盖已有值）")
            alert.addButton(withTitle: "取消")
            let response = alert.runModal()
            guard response == .alertFirstButtonReturn || response == .alertSecondButtonReturn else { return }
            let mode: ExchangeMergeMode = response == .alertFirstButtonReturn ? .fillEmpty : .overwrite

            isExchangeRunning = true
            defer { isExchangeRunning = false }
            var summary = try SettingsExchange.apply(plan: plan, mode: mode, catalog: catalog)
            summary.settingsApplied = SettingsExchange.apply(settings: plan.settings, to: settings)
            Task { await reloadNow(reason: "导入设置与图片数据") }
            showStatus("导入完成：\(summary.descriptionText)")
            AppLog.breadcrumb("导入设置与图片数据：\(summary.descriptionText) ← \(url.lastPathComponent)")
        } catch {
            presentedError = error.localizedDescription
            AppLog.error("导入设置与图片数据失败：\(error.localizedDescription)")
        }
    }

    private static func exchangeTimestamp() -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        return formatter.string(from: Date())
    }

    /// 备份当前素材库（catalog.db + 缩略图）到用户选择的目录。
    func backupLibrary() {
        guard let dataDirectory, catalog != nil else {
            showStatus("先打开一个素材库")
            return
        }
        let panel = NSOpenPanel()
        panel.title = "选择备份保存位置"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "备份到此"
        guard panel.runModal() == .OK, let destination = panel.url else { return }
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        showStatus("正在备份素材库…")
        Task {
            do {
                let summary = try await Task.detached(priority: .userInitiated) {
                    try LibraryBackup.create(from: dataDirectory, destination: destination, appVersion: version)
                }.value
                showStatus("备份完成：\(summary.descriptionText)")
                AppLog.breadcrumb("备份素材库：\(summary.descriptionText) → \(summary.destination.path)")
                NSWorkspace.shared.activateFileViewerSelecting([summary.destination])
            } catch {
                presentedError = error.localizedDescription
                AppLog.error("备份素材库失败：\(error.localizedDescription)")
            }
        }
    }

    /// 从备份目录恢复素材库（替换 catalog.db + 缩略图）。
    func restoreLibrary() {
        guard let dataDirectory else {
            showStatus("先打开一个素材库")
            return
        }
        let panel = NSOpenPanel()
        panel.title = "选择 STJMediaViewer-Backup-* 备份目录"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "恢复"
        guard panel.runModal() == .OK, let backup = panel.url else { return }
        let alert = NSAlert()
        alert.messageText = "用备份替换当前素材库？"
        alert.informativeText = "当前 catalog.db 会改名保留（catalog.db.restore-时间戳），缩略图缓存会被备份覆盖。"
        alert.addButton(withTitle: "恢复")
        alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        showStatus("正在恢复素材库…")
        Task {
            do {
                let summary = try await Task.detached(priority: .userInitiated) {
                    try LibraryBackup.restore(from: backup, to: dataDirectory)
                }.value
                showStatus("恢复完成：\(summary.descriptionText)")
                AppLog.breadcrumb("恢复素材库：\(summary.descriptionText)")
                await open(directory: dataDirectory)
            } catch {
                presentedError = error.localizedDescription
                AppLog.error("恢复素材库失败：\(error.localizedDescription)")
            }
        }
    }

    /// 导出选中素材的带水印 JPEG（导出到用户选择目录）。
    func exportWatermarkedSelection() {
        guard !selectedAssets.isEmpty else {
            showStatus("先选择要导出的素材")
            return
        }
        let style = settings.watermarkStyle
        guard style.isEnabled else {
            showStatus("请先在设置 → 媒体库中填写水印文字")
            return
        }
        let panel = NSOpenPanel()
        panel.title = "选择水印导出目录"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "导出"
        guard panel.runModal() == .OK, let directory = panel.url else { return }
        let assets = selectedAssets
        AppLog.breadcrumb("导出带水印：\(assets.count) 个素材 → \(directory.path)")
        Task {
            do {
                let summary = try await Task.detached(priority: .userInitiated) {
                    try Watermarker.export(assets: assets, to: directory, style: style)
                }.value
                showStatus("水印导出：\(summary.descriptionText)")
                AppLog.breadcrumb("水印导出完成：\(summary.descriptionText) → \(summary.directory.path)")
                if !summary.failures.isEmpty {
                    presentedError = summary.failures.joined(separator: "\n")
                }
                NSWorkspace.shared.open(directory)
            } catch {
                presentedError = error.localizedDescription
                AppLog.error("水印导出失败：\(error.localizedDescription)")
            }
        }
    }

    func exportPairs() {
        let targets = selectedAssets
        guard !targets.isEmpty else { return }
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "导出到此处"
        panel.message = "选择导出目标文件夹（会一并复制配对的 JPG / RAW 文件）"
        guard panel.runModal() == .OK, let directory = panel.url else { return }

        var pairs: [ExportPair] = []
        for asset in targets {
            let pair = PairExporter.plan(for: asset)
            if !pair.isEmpty { pairs.append(pair) }
        }
        guard !pairs.isEmpty else {
            showStatus("选中的素材没有可导出的文件")
            return
        }
        Task {
            do {
                let written = try PairExporter.copy(pairs, to: directory)
                AppLog.breadcrumb("导出配对：\(written.count) 个文件 → \(directory.path)")
                showStatus("已导出 \(written.count) 个文件到 \(directory.lastPathComponent)")
            } catch {
                AppLog.error("导出配对失败：\(error.localizedDescription)")
                presentedError = error.localizedDescription
            }
        }
    }

    // MARK: 合集

    func createCollection() {
        guard let catalog else { return }
        let alert = NSAlert()
        alert.messageText = "新建合集"
        alert.informativeText = "合集用于把跨目录的素材组织在一起。"
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        field.placeholderString = "合集名称"
        alert.accessoryView = field
        alert.addButton(withTitle: "创建")
        alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        let name = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return }
        do {
            let id = try catalog.createCollection(name: name)
            AppLog.breadcrumb("新建合集：\(name)")
            sidebarSelection = .collection(id)
            showStatus("已创建合集“\(name)”")
        } catch {
            AppLog.error("新建合集失败：\(error.localizedDescription)")
            presentedError = error.localizedDescription
        }
    }

    func addSelectionToCollection(_ collectionID: String) {
        guard let catalog else { return }
        let ids = selectedAssets.map(\.id)
        guard !ids.isEmpty else { return }
        Task {
            do {
                let expanded = try catalog.expandedIDs(for: ids)
                try catalog.addToCollection(collectionID: collectionID, assetIDs: expanded)
                AppLog.breadcrumb("加入合集 \(collectionID)：\(ids.count) 项（含组成员 \(expanded.count) 条）")
                showStatus("已把 \(ids.count) 项加入合集")
                await reloadNow(reason: "加入合集")
            } catch {
                AppLog.error("加入合集失败：\(error.localizedDescription)")
                presentedError = error.localizedDescription
            }
        }
    }

    func removeSelectionFromCollection() {
        guard let catalog, case .collection(let collectionID) = sidebarSelection else { return }
        let ids = selectedAssets.map(\.id)
        Task {
            do {
                try catalog.removeFromCollection(collectionID: collectionID, assetIDs: ids)
                AppLog.breadcrumb("移出合集 \(collectionID)：\(ids.count) 项")
                showStatus("已移出合集 \(ids.count) 项")
                await reloadNow(reason: "移出合集")
            } catch {
                AppLog.error("移出合集失败：\(error.localizedDescription)")
                presentedError = error.localizedDescription
            }
        }
    }

    func renameCollection(_ collection: AssetCollection) {
        guard let catalog else { return }
        let alert = NSAlert()
        alert.messageText = "重命名合集"
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        field.stringValue = collection.name
        alert.accessoryView = field
        alert.addButton(withTitle: "保存")
        alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        do {
            try catalog.renameCollection(id: collection.id, name: field.stringValue)
            showStatus("合集已重命名")
            Task { await reloadNow(reason: "重命名合集") }
        } catch {
            presentedError = error.localizedDescription
        }
    }

    func deleteCollection(_ collection: AssetCollection) {
        guard let catalog else { return }
        let alert = NSAlert()
        alert.messageText = "删除合集“\(collection.name)”？"
        alert.informativeText = "只删除合集及其成员关系，不会删除素材文件。"
        alert.addButton(withTitle: "删除")
        alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        do {
            try catalog.deleteCollection(id: collection.id)
            if case .collection(let id) = sidebarSelection, id == collection.id {
                sidebarSelection = .section(.all)
            }
            Task { await reloadNow(reason: "删除合集") }
        } catch {
            presentedError = error.localizedDescription
        }
    }

    // MARK: 合并组

    func toggleGroupExpansion(_ group: MergeGroup) {
        if expandedGroup?.id == group.id {
            expandedGroup = nil
        } else {
            expandedGroup = group
        }
    }

    // MARK: 拖拽

    // MARK: 画廊

    func openGallery(_ asset: Asset? = nil) {
        guard let target = asset ?? primarySelection else {
            showStatus("请先选择一条素材再打开画廊")
            return
        }
        selection = [target.id]
        galleryAssetID = target.id
        AppLog.breadcrumb("打开画廊：\(target.fileName)")
    }

    func closeGallery() {
        guard galleryAssetID != nil else { return }
        galleryAssetID = nil
        AppLog.breadcrumb("关闭画廊")
    }

    /// 在画廊中向前后翻页（delta = ±1），到头即停。
    func stepGallery(by delta: Int) {
        guard let current = galleryAssetID,
              let index = assets.firstIndex(where: { $0.id == current }) else { return }
        let next = min(max(index + delta, 0), max(assets.count - 1, 0))
        let asset = assets[next]
        galleryAssetID = asset.id
        selection = [asset.id]
    }

    /// 显示偏好（例如 RAW+JPG 折叠）变化后重新加载列表。
    func reloadForPreferenceChange() {
        scheduleReload(reason: "显示偏好变化")
    }

    /// 本次拖拽要带上的文件 URL（拖选中项之一 → 整批；拖未选中 → 只拖它）。
    func dragURLs(for asset: Asset) -> [URL] {
        let ids = DragPayload.assetIDs(dragging: asset.id, selection: selection)
        let byID = Dictionary(uniqueKeysWithValues: assets.map { ($0.id, $0) })
        let urls = ids.compactMap { byID[$0]?.fileURL }.filter { FileManager.default.fileExists(atPath: $0.path) }
        return urls.isEmpty ? [asset.fileURL] : urls
    }

    // MARK: 提示

    /// 复制所选素材的完整路径（每行一个），与 Windows 版右键行为一致。
    func copySelectionPaths() {
        let paths = selectedAssets.map { $0.fileURL.path }
        guard !paths.isEmpty else { return }
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(paths.joined(separator: "\n"), forType: .string)
        showStatus(paths.count == 1 ? "已复制路径" : "已复制 \(paths.count) 个路径")
    }

    /// 复制所选素材的文件名（每行一个）。
    func copySelectionFileNames() {
        let names = selectedAssets.map { $0.fileName }
        guard !names.isEmpty else { return }
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(names.joined(separator: "\n"), forType: .string)
        showStatus(names.count == 1 ? "已复制文件名" : "已复制 \(names.count) 个文件名")
    }

    // MARK: 检查更新

    /// 当前应用版本（Info.plist 里的 CFBundleShortVersionString）。
    var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    /// 检查更新。`interactive` 为 false 时静默失败（启动自动检查用）。
    func checkForUpdates(interactive: Bool = true) {
        guard !isCheckingUpdate else { return }
        isCheckingUpdate = true
        updateCheckMessage = interactive ? "正在检查更新…" : nil
        Task { [weak self] in
            guard let self else { return }
            defer { Task { @MainActor in self.isCheckingUpdate = false } }
            do {
                let version = self.currentVersion
                let update = try await UpdateChecker.check(current: version)
                await MainActor.run {
                    self.availableUpdate = update
                    if let update {
                        self.updateCheckMessage = nil
                        if interactive {
                            self.showStatus("发现新版本 \(update.version)，可在发行版页面下载")
                        }
                        AppLog.info("检查更新：当前 \(version)，最新 \(update.version)")
                    } else {
                        self.updateCheckMessage = "已是最新版本（\(version)）"
                        if interactive { self.showStatus("已是最新版本（\(version)）") }
                    }
                    UserDefaults.standard.set(Date().timeIntervalSince1970, forKey: "lastUpdateCheckAt")
                }
            } catch {
                await MainActor.run {
                    self.updateCheckMessage = interactive ? "检查失败：\(error.localizedDescription)" : nil
                    if interactive {
                        self.showStatus("检查更新失败：\(error.localizedDescription)")
                    }
                    AppLog.error("检查更新失败：\(error.localizedDescription)")
                }
            }
        }
    }

    /// 打开发行版页面，让用户下载新版 dmg。
    func openUpdatePage() {
        let url = availableUpdate?.releaseURL
            ?? URL(string: "https://gitcode.com/\(UpdateChecker.repository)/releases")!
        NSWorkspace.shared.open(url)
    }

    /// 启动时每天自动检查一次（不打扰：仅在发现新版时提示）。
    func autoCheckUpdatesIfNeeded() {
        let last = UserDefaults.standard.double(forKey: "lastUpdateCheckAt")
        let day: TimeInterval = 24 * 60 * 60
        guard Date().timeIntervalSince1970 - last > day else { return }
        checkForUpdates(interactive: false)
    }

    // MARK: 云同步（WebDAV）

    /// 上传所选素材（含同目录 XMP 边车）到 WebDAV；未配置时提示去设置。
    func uploadSelectionToWebDAV() {
        guard !selectedAssets.isEmpty else {
            showStatus("先在网格里选择要上传的素材")
            return
        }
        guard !isCloudSyncing else { return }
        guard let credentials = SettingsStore.shared.webdavCredentials() else {
            showStatus("请先在「设置 → 云同步」填写 WebDAV 地址、用户名与密码")
            AppLog.error("云同步未配置：缺少 WebDAV 地址或口令")
            return
        }
        let layout = SettingsStore.shared.cloudLayout
        let (items, skipped) = CloudSync.plan(assets: selectedAssets, layout: layout)
        guard !items.isEmpty else {
            showStatus("所选素材的文件都不存在")
            return
        }

        isCloudSyncing = true
        cloudSyncProgress = CloudSyncProgress(completed: 0, total: items.count,
                                              currentItem: "", bytesUploaded: 0, failed: 0)
        AppLog.network(method: "PUT", url: credentials.endpoint.absoluteString,
                       status: nil, bytes: 0, milliseconds: 0,
                       note: "开始上传 \(items.count) 个文件（布局：\(layout.title)）")
        cloudSyncTask = Task { [weak self] in
            var throttle: Duration = .zero
            #if DEBUG
            let ms = UserDefaults.standard.integer(forKey: "cloudSyncThrottleMS")
            if ms > 0 { throttle = .milliseconds(ms) }
            #endif
            let result = await CloudSync.upload(items: items, credentials: credentials,
                                                progress: { progress in
                Task { @MainActor [weak self] in
                    self?.cloudSyncProgress = progress
                }
            }, interItemDelay: throttle)
            await MainActor.run { [weak self] in
                guard let self else { return }
                self.isCloudSyncing = false
                self.cloudSyncProgress = nil
                self.cloudSyncSummary = result.summaryText
                if !result.failed.isEmpty, let first = result.failed.first {
                    self.presentedError = "部分素材上传失败：\(first.name) — \(first.message)"
                }
                if !skipped.isEmpty {
                    AppLog.error("云同步跳过 \(skipped.count) 个不存在的文件：\(skipped.prefix(3).joined(separator: ", "))")
                }
                self.logSyncResult(result, layout: layout)
                self.showStatus(result.summaryText)
            }
        }
    }

    private func logSyncResult(_ result: CloudSyncResult, layout: CloudLayout) {
        AppLog.breadcrumb("云同步：\(result.summaryText)（布局 \(layout.title)，耗时 \(String(format: "%.1f", result.duration))s）")
        AppLog.network(method: "PUT", url: SettingsStore.shared.webdavEndpoint,
                       status: result.failed.isEmpty ? 200 : 500,
                       bytes: Int(result.bytesUploaded), milliseconds: Int(result.duration * 1000),
                       note: "上传 \(result.uploaded.count) 项，失败 \(result.failed.count) 项")
    }

    func cancelCloudSync() {
        cloudSyncTask?.cancel()
        cloudSyncTask = nil
        isCloudSyncing = false
        cloudSyncProgress = nil
        showStatus("已取消云同步（已完成的部分保留在云端）")
        AppLog.breadcrumb("云同步：用户取消")
    }


    // MARK: - 云相册（WebDAV）

    /// 打开云相册：远端浏览 + 预览 + 导入 + 上传，替代原先分散的「上传」与「浏览云端」两个入口。
    func presentCloudAlbum() {
        guard SettingsStore.shared.webdavCredentials() != nil else {
            presentedError = "请先在设置 → 云同步里填写 WebDAV 地址与账号口令"
            return
        }
        isCloudAlbumPresented = true
        AppLog.breadcrumb("打开云相册")
    }

    /// 设置页「测试连接」：跑连接→鉴权→列目录→上传探针→核验→清理。
    func runWebDAVDiagnostics() async -> [DiagnosticStep] {
        guard let credentials = SettingsStore.shared.webdavCredentials() else {
            let step = DiagnosticStep(id: 0, title: "配置", detail: "请先填写 WebDAV 地址与口令", succeeded: false)
            cloudDiagnostics = [step]
            return [step]
        }
        let steps = await WebDAVDiagnostics.run(credentials: credentials)
        cloudDiagnostics = steps
        for step in steps {
            AppLog.breadcrumb("WebDAV 诊断：\(step.title) — \(step.succeeded ? "OK" : "失败") \(step.detail)")
        }
        return steps
    }

    func showStatus(_ message: String) {
        statusMessage = message
        statusTask?.cancel()
        statusTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 9_000_000_000)
            guard !Task.isCancelled else { return }
            self?.statusMessage = nil
        }
    }

    // MARK: 重复项检测

    /// 扫描当前素材库里的疑似重复（先按大小/尺寸/时长聚类，再按文件头哈希确认）。
    func findDuplicates() {
        guard let catalog, !isDuplicateScanning else { return }
        isDuplicateScanning = true
        AppLog.breadcrumb("查找重复项：开始扫描")
        Task {
            do {
                let all = try catalog.loadAssets(limit: 200_000)
                let groups = await Task.detached(priority: .userInitiated) {
                    DuplicateFinder.find(in: all, verifyContent: true)
                }.value
                duplicateGroups = groups
                isDuplicateSheetPresented = true
                let wasted = groups.reduce(Int64(0)) { $0 + $1.wastedBytes }
                AppLog.breadcrumb("查找重复项：\(groups.count) 组，可回收 \(ByteCountFormatter.string(fromByteCount: wasted, countStyle: .file))")
                showStatus(groups.isEmpty
                    ? "没有发现重复素材"
                    : "发现 \(groups.count) 组重复，可回收 \(ByteCountFormatter.string(fromByteCount: wasted, countStyle: .file))")
            } catch {
                AppLog.error("查找重复项失败：\(error.localizedDescription)")
                presentedError = error.localizedDescription
            }
            isDuplicateScanning = false
        }
    }

    func selectDuplicateGroup(_ group: DuplicateGroup) {
        selection = Set(group.assets.map(\.id))
        isDuplicateSheetPresented = false
    }

    // MARK: 整理动作

    /// 选择目标目录并填写重命名模板后执行复制/移动。
    func organizeSelection(mode: OrganizeMode) {
        guard catalog != nil, !selectedAssets.isEmpty, !isOrganizing else { return }
        let targets = selectedAssets
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = "选择目标目录"
        panel.message = "\(mode.title) \(targets.count) 个素材到："
        guard panel.runModal() == .OK, let destination = panel.url else { return }

        let field = NSTextField(string: "{date}/{original}")
        field.frame = NSRect(x: 0, y: 0, width: 320, height: 24)
        let alert = NSAlert()
        alert.messageText = "重命名模板（\(mode.title)）"
        alert.informativeText = "可用占位符：{original} {name} {ext} {date} {year} {month} {day} {city} {country} {device} {rating} {theme}；用 / 分目录。重名自动加序号。"
        alert.accessoryView = field
        alert.addButton(withTitle: "开始")
        alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        let template = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        runOrganize(assets: targets, mode: mode, destination: destination,
                    template: template.isEmpty ? "{original}" : template)
    }

    func runOrganize(assets targets: [Asset], mode: OrganizeMode, destination: URL, template: String) {
        guard let catalog, !targets.isEmpty, !isOrganizing else { return }
        isOrganizing = true
        let rules = OrganizeRules(template: template, conflictPolicy: .autoRename)
        let root = dataDirectory
        Task {
            let summary = await Task.detached(priority: .userInitiated) {
                Organizer.perform(assets: targets, to: destination, mode: mode, rules: rules)
            }.value
            if mode == .move, !summary.relocated.isEmpty {
                do {
                    try catalog.updateLocations(summary.relocated, relativeTo: root)
                } catch {
                    AppLog.error("整理后回写数据库失败：\(error.localizedDescription)")
                    presentedError = error.localizedDescription
                }
            }
            organizeSummary = summary.descriptionText
            AppLog.breadcrumb("整理：\(summary.descriptionText) → \(destination.path)")
            if let first = summary.failures.first {
                presentedError = "部分文件整理失败：\(first.message)"
            }
            showStatus(summary.descriptionText)
            isOrganizing = false
            if mode == .move {
                await reloadNow(reason: "整理素材")
            }
        }
    }
}

// MARK: - 快照

private struct Snapshot: Sendable {
    let assets: [Asset]
    let total: Int
    let facets: Facets
    let collections: [AssetCollection]
    let mergeGroups: [MergeGroup]
    let libraries: [LibraryRoot]
}

// MARK: - 记住上次素材库

enum LastLibraryStore {
    private static let key = "lastLibraryDirectory"

    static var lastDirectoryPath: String? {
        get {
            let path = UserDefaults.standard.string(forKey: key)
            guard let path, FileManager.default.fileExists(atPath: path + "/catalog.db") else { return nil }
            return path
        }
        set {
            UserDefaults.standard.set(newValue, forKey: key)
        }
    }
}